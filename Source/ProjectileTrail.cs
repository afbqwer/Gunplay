using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Gunplay
{
    class ProjectileTrail : ThingWithComps
    {
        Matrix4x4 drawingMatrix = default;

        int ticksUtilDeath;
        int ticksIdle;
        Projectile projectile;
        Vector3 a;
        Vector3 exactPosition;
        Vector3 previousPosition;
        float speed;
        Vector3 dir;
        float length;
        float width;
        float traveled;
        float altitude;
        float ticksPerLength;

        public void Initialize(Projectile proj, Vector3 destination, Thing equipment)
        {
            ticksUtilDeath = -1;
            ticksIdle = 0;
            projectile = proj;
            a = projectile.ExactPosition;
            Vector3 b = destination;
            altitude = proj.def.Altitude;
            a.y = b.y = altitude;
            speed = proj.def.projectile.SpeedTilesPerTick;
            exactPosition = a;
            previousPosition = a;
            traveled = 0f;

            length = speed * 15f;
            ticksPerLength = speed > 0f ? length / speed : 0f;
            dir = (b - a).normalized;
            if (Gunplay.settings.trailWidthMode == TrailWidthMode.Texture)
            {
                // 投射物贴图经 GridPlane(drawSize) 旋转绘制，drawSize.x 为垂直于飞行方向的横向宽度
                GraphicData graphicData = projectile.def.graphicData;
                width = (graphicData != null ? graphicData.drawSize.x : 1f) * 0.1f;
            }
            else
            {
                width = Mathf.Min(projectile.DamageAmount, 50f) * 0.006f;
            }

            ProjectileTrailDef trailDef = def as ProjectileTrailDef;
            if (trailDef != null && trailDef.trailWidth != -1) width = trailDef.trailWidth;

            width *= Gunplay.settings.trailWidthFactor;
        }


        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_References.Look(ref projectile, "projectile");
            Scribe_Values.Look(ref a, "a");
            Scribe_Values.Look(ref exactPosition, "exactPosition");
            Scribe_Values.Look(ref previousPosition, "previousPosition");
            Scribe_Values.Look(ref speed, "speed");
            Scribe_Values.Look(ref dir, "dir");
            Scribe_Values.Look(ref length, "length");
            Scribe_Values.Look(ref width, "width");
            Scribe_Values.Look(ref traveled, "traveled");
            Scribe_Values.Look(ref altitude, "altitude");
            Scribe_Values.Look(ref ticksPerLength, "ticksPerLength");
            Scribe_Values.Look(ref ticksIdle, "ticksIdle");
            Scribe_Values.Look(ref ticksUtilDeath, "ticksUtilDeath");
        }

        public override Vector3 DrawPos
        {
            get
            {
                return exactPosition;
            }
        }

        void ApplyPosition(Vector3 pos)
        {
            pos.y = altitude;
            Vector3 delta = pos - previousPosition;
            if (delta.sqrMagnitude > 0.000001f)
            {
                traveled += delta.magnitude;
                dir = delta.normalized;
            }
            else
            {
                ticksIdle++;
            }
            exactPosition = pos;
        }

        public void CaptureFinalPosition(Vector3 pos)
        {
            if (projectile == null) return;

            ApplyPosition(pos);
            projectile = null;
            ticksUtilDeath = Mathf.CeilToInt(ticksPerLength);
        }

        protected override void Tick()
        {
            base.Tick();

            if (projectile != null)
            {
                if (projectile.Destroyed || (ticksUtilDeath == -1 && ticksIdle > 10))
                {
                    projectile = null;
                    ticksUtilDeath = Mathf.CeilToInt(ticksPerLength);
                }
                else
                {
                    // 只在投射物存活时读取位置；销毁后不再访问它
                    ApplyPosition(projectile.ExactPosition);
                }
            }

            previousPosition = exactPosition;

            IntVec3 cell = exactPosition.ToIntVec3();
            if (cell != Position)
                Position = cell;

            if (ticksUtilDeath > 0)
                ticksUtilDeath--;
            else if (ticksUtilDeath == 0)
                Destroy(DestroyMode.Vanish);
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (length <= 0f || dir.sqrMagnitude < 0.000001f)
            {
                Comps_PostDraw();
                return;
            }

            float len = Mathf.Min(traveled, length);
            if (ticksUtilDeath != -1)
                len *= ticksUtilDeath / ticksPerLength;

            if (len <= 0f)
            {
                Comps_PostDraw();
                return;
            }

            Vector3 drawPos = exactPosition - dir * len / 2;
            Vector3 drawingScale = new Vector3(width, 1f, len);
            drawingMatrix.SetTRS(drawPos, Quaternion.LookRotation(dir), drawingScale);
            Graphics.DrawMesh(MeshPool.plane10, drawingMatrix, Graphic.MatSingle, 0);

            Comps_PostDraw();
        }

    }
}

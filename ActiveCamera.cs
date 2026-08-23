using GTA;
using GTA.Math;

namespace FlockSurveillance
{
    public sealed class ActiveCamera
    {
        public CameraDefinition Definition { get; set; }
        public Vector3 Position { get; set; }
        public Prop Prop { get; set; }
        public Blip CameraBlip { get; set; }
        public Blip ConeBlip { get; set; }
        public bool WasReportableSighting { get; set; }
        public int WeaponHitCount { get; set; }
    }
}
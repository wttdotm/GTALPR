namespace FlockSurveillance
{
    public sealed class CameraDefinition
    {
        public string FlockCameraId { get; set; }
        public string osmType { get; set; }
        public long osmId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Heading { get; set; }
        public bool IsDestroyed { get; set; }
    }
}
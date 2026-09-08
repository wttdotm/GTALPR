namespace FlockSurveillance
{
    public sealed class CameraDefinition
    {
        public string FlockCameraId { get; set; }
        public string CameraId
        {
            get { return FlockCameraId; }
            set { FlockCameraId = value; }
        }
        public string osmType { get; set; }
        public string osmId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Heading { get; set; }
        public bool IsDestroyed { get; set; }
    }
}

namespace BLEWorkerService.Models
{
    public class StrokeData
    {
        public int X { get; set; }
        public int Y { get; set; }
        public DateTime Timestamp { get; set; }

        public StrokeData(int x, int y)
        {
            X = x;
            Y = y;
            Timestamp = DateTime.UtcNow;
        }
    }
}
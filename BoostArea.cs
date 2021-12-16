using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BrianMed.SmartCrop
{
    public class BoostArea
    {
        public BoostArea(Rectangle area, float weight)
        {
            this.Area = area;
            this.Weight = weight;
        }

        public Rectangle Area { get; set; }
        public float Weight { get; set; }
    }
}

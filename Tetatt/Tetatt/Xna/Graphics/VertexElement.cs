using System;

namespace Microsoft.Xna.Framework.Graphics
{
    [Serializable]
    public struct VertexElement
    {
        public int Offset { get; set; }
        public VertexElementFormat ElementFormat { get; set; }
        public VertexElementUsage ElementUsage { get; set; }
        public int UsageIndex { get; set; }

        public VertexElement(int offset,
                             VertexElementFormat elementFormat,
                             VertexElementUsage elementUsage,
                             int usageIndex)
        {
            this.Offset = offset;
            this.ElementFormat = elementFormat;
            this.ElementUsage = elementUsage;
            this.UsageIndex = usageIndex;
        }
    }
}

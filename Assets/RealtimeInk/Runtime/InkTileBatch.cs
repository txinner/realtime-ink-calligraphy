using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RealtimeInk
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DropletStamp
    {
        public Vector4 shape;    // center.xy, radius, longitudinal scale
        public Vector4 deposit;  // tangent.xy, pigment dose, water dose
        public Vector4 variation;  // noise amplitude, stroke seed, soluble fraction, transverse scale
        public Vector4 profile;    // roundness, tip power, chip strength, noise type (0/1)
    }

    /// <summary>CPU 只做包围盒分桶，不生成像素。每个 GPU 像素按输入顺序遍历所属瓦片的脚印。</summary>
    internal sealed class InkTileBatch : IDisposable
    {
        public const int TileSize = 16;
        public const int BatchCapacity = 256;
        public int ActiveCount { get; private set; }
        public int TileColumns { get; }
        public ComputeBuffer Stamps { get; }
        public ComputeBuffer Offsets { get; }
        public ComputeBuffer Indices { get; private set; }
        public ComputeBuffer ActiveTiles { get; }

        readonly int tileRows, width, height;
        readonly DropletStamp[] stamps = new DropletStamp[BatchCapacity];
        readonly RectInt[] bounds = new RectInt[BatchCapacity];
        readonly int[] counts, offsets, cursors, active;
        int[] indices = new int[4096];

        public InkTileBatch(int width, int height)
        {
            this.width = width; this.height = height;
            TileColumns = (width + TileSize - 1) / TileSize;
            tileRows = (height + TileSize - 1) / TileSize;
            int tiles = TileColumns * tileRows;
            counts = new int[tiles]; offsets = new int[tiles + 1];
            cursors = new int[tiles]; active = new int[tiles];
            Stamps = new ComputeBuffer(BatchCapacity, 64);
            Offsets = new ComputeBuffer(tiles + 1, sizeof(int));
            ActiveTiles = new ComputeBuffer(tiles, sizeof(int));
            Indices = new ComputeBuffer(indices.Length, sizeof(int));
        }

        public void Upload(IList<DropletStamp> source, int begin, int count)
        {
            Array.Clear(counts, 0, counts.Length);
            for (int i = 0; i < count; i++)
            {
                DropletStamp s = stamps[i] = source[begin + i];
                Vector2 dir = new Vector2(s.deposit.x, s.deposit.y);
                float extentX = s.shape.z * (Mathf.Abs(dir.x) * s.shape.w + Mathf.Abs(dir.y) * s.variation.w);
                float extentY = s.shape.z * (Mathf.Abs(dir.y) * s.shape.w + Mathf.Abs(dir.x) * s.variation.w);
                // 包含噪声轮廓偏移及抗锯齿区域。
                extentX = extentX * 1.25f + 3f; extentY = extentY * 1.25f + 3f;
                int minX = Mathf.Clamp(Mathf.FloorToInt((s.shape.x - extentX) / TileSize), 0, TileColumns - 1);
                int maxX = Mathf.Clamp(Mathf.FloorToInt((s.shape.x + extentX) / TileSize), 0, TileColumns - 1);
                int minY = Mathf.Clamp(Mathf.FloorToInt((s.shape.y - extentY) / TileSize), 0, tileRows - 1);
                int maxY = Mathf.Clamp(Mathf.FloorToInt((s.shape.y + extentY) / TileSize), 0, tileRows - 1);
                bool outside = s.shape.x + extentX < 0 || s.shape.y + extentY < 0 ||
                    s.shape.x - extentX >= width || s.shape.y - extentY >= height;
                bounds[i] = outside ? new RectInt(0, 0, 0, 0) :
                    new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
                for (int y = bounds[i].yMin; y < bounds[i].yMax; y++)
                    for (int x = bounds[i].xMin; x < bounds[i].xMax; x++) counts[y * TileColumns + x]++;
            }
            ActiveCount = 0;
            offsets[0] = 0;
            for (int t = 0; t < counts.Length; t++)
            {
                offsets[t + 1] = offsets[t] + counts[t];
                cursors[t] = offsets[t];
                if (counts[t] > 0) active[ActiveCount++] = t;
            }
            int total = offsets[counts.Length];
            if (total > indices.Length)
            {
                indices = new int[Mathf.NextPowerOfTwo(total)];
                Indices.Dispose();
                Indices = new ComputeBuffer(indices.Length, sizeof(int));
            }
            for (int i = 0; i < count; i++)
                for (int y = bounds[i].yMin; y < bounds[i].yMax; y++)
                    for (int x = bounds[i].xMin; x < bounds[i].xMax; x++)
                        indices[cursors[y * TileColumns + x]++] = i;
            Stamps.SetData(stamps, 0, 0, count);
            Offsets.SetData(offsets);
            if (total > 0) Indices.SetData(indices, 0, 0, total);
            if (ActiveCount > 0) ActiveTiles.SetData(active, 0, 0, ActiveCount);
        }

        public void Dispose()
        {
            Stamps.Dispose(); Offsets.Dispose(); Indices.Dispose(); ActiveTiles.Dispose();
        }
    }
}

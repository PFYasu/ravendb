﻿using System.Runtime.InteropServices;

namespace Voron.Data.Sets
{
    /*
     * Format of a set leaf page:
     *
     * PageHeader       - 64 bytes
     * 0 - 64 bytes    -  short[16] PositionsOfCompressedEntries; (sorted by value)
     * 
     * actual compressed entries
     */
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = PageHeader.SizeOf)]
    public unsafe struct SetLeafPageHeader
    {
        [FieldOffset(0)]
        public long PageNumber;

        [FieldOffset(8)]
        public ushort NumberOfCompressedPositions;

        [FieldOffset(10)]
        public ushort Ceiling;

        [FieldOffset(12)]
        public PageFlags Flags;

        [FieldOffset(13)]
        public ExtendedPageType SetFlags;
        
        [FieldOffset(14)]
        private fixed byte Reserved[2];
        
        [FieldOffset(16)]
        public long Baseline;

        [FieldOffset(24)]
        public int NumberOfEntries;

        public int Floor => PageHeader.SizeOf + (NumberOfCompressedPositions * sizeof(SetLeafPage.CompressedHeader));
    }
}

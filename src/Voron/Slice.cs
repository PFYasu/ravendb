﻿using Sparrow;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Voron
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Slice
    {
        public ByteString Content;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Slice(SliceOptions options, ByteString content)
        {
            this.Content = content;
            Content.SetUserDefinedFlags((ByteStringType)options);               
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Slice(ByteString content)
        {
            this.Content = content;
        }

        public bool HasValue
        {
            get { return Content.HasValue; }
        }

        public int Size
        {
            get
            {
                Debug.Assert(Content.Length >= 0);
                return Content.Length;
            }
        }

        public SliceOptions Options
        {
            get
            {
                return (SliceOptions) (Content.Flags & ByteStringType.UserDefinedMask);
            }
        }

        public byte this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(Content.Ptr != null, "Uninitialized slice!");

                if (!Content.HasValue)
                    throw new InvalidOperationException("Uninitialized slice!");

                return *(Content.Ptr + (sizeof(byte) * index));
            }
        }

        public Slice Clone(ByteStringContext context, ByteStringType type = ByteStringType.Mutable)
        {
            return new Slice(context.Clone(this.Content, type));
        }

        public Slice Skip(ByteStringContext context, int bytesToSkip, ByteStringType type = ByteStringType.Mutable)
        {
            return new Slice(context.Skip(this.Content, bytesToSkip, type));       
        }

        public void CopyTo(int from, byte* dest, int offset, int count)
        {
            this.Content.CopyTo(from, dest, offset, count);
        }

        public void CopyTo(byte* dest)
        {
            this.Content.CopyTo(dest);
        }   
         
        public void CopyTo(byte[] dest)
        {
            this.Content.CopyTo(dest);
        }

        public void CopyTo(int from, byte[] dest, int offset, int count)
        {
            this.Content.CopyTo(from, dest, offset, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Slice From(ByteStringContext context, string value, ByteStringType type = ByteStringType.Mutable)
        {
            return new Slice(context.From(value, type));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Slice From(ByteStringContext context, byte[] value, ByteStringType type = ByteStringType.Mutable)
        {
            return new Slice(context.From(value, 0, value.Length, type));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Slice From(ByteStringContext context, byte[] value, int offset, int count, ByteStringType type = ByteStringType.Mutable)
        {
            return new Slice(context.From(value, offset, count, type));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Slice From(ByteStringContext context, byte* value, int size, ByteStringType type = ByteStringType.Mutable)
        {
            return new Slice(context.From(value, size, type));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ByteStringContext.Scope External(ByteStringContext context, byte* value, int size,
            out Slice slice)
        {
            return External(context, value, size,  ByteStringType.Mutable | ByteStringType.External, out slice);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ByteStringContext.Scope External(ByteStringContext context, byte* value, int size,
            ByteStringType type,
            out Slice slice)
        {
            ByteString str;
            var scope= context.FromPtr(value, size, type | ByteStringType.External, out str);
            slice = new Slice(str);
            return scope;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseExternal(ByteStringContext context)
        {
            context.ReleaseExternal(ref Content);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release(ByteStringContext context)
        {
            context.Release(ref Content);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueReader CreateReader()
        {
            return new ValueReader(Content.Ptr, Size);
        }

        public override int GetHashCode()
        {
            return this.Content.GetHashCode();
        }

        public override string ToString()
        {
            return this.Content.ToString(Encoding.UTF8);
        }
    }

    public static class Slices
    {
        private static readonly ByteStringContext _sharedSliceContent = new ByteStringContext();

        public static readonly Slice AfterAllKeys;
        public static readonly Slice BeforeAllKeys;
        public static readonly Slice Empty;

        static Slices()
        {
            Empty = new Slice(SliceOptions.Key, _sharedSliceContent.From(string.Empty));
            BeforeAllKeys = new Slice(SliceOptions.BeforeAllKeys, _sharedSliceContent.From(string.Empty));
            AfterAllKeys = new Slice(SliceOptions.AfterAllKeys, _sharedSliceContent.From(string.Empty));
        }
    }
}
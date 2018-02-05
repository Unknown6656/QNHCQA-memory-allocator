using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System;

namespace QNHCQA
{
#if USE_64BIT
    using __size = Int64;
#elif USE_UNSIGNED
    using __size = UInt32;
#else
    using __size = Int32;
#endif
    using static SystemInformation;


    internal unsafe delegate void* Alloc(long size);
    internal unsafe delegate void Free(void* ptr);


    public static unsafe class SystemInformation
    {
#if USE_PLATFORM_SPECIFIC_MALLOC
        #region UNMANAGED

        private static readonly Dictionary<long, long> __winheaphnds = new Dictionary<long, long>();

        [DllImport("kernel32.dll", EntryPoint = "HeapAlloc", SetLastError = false)]
        internal static extern void* __win__malloc(void* hHeap, uint dwFlags, long dwBytes);

        [DllImport("libc.so", EntryPoint = "malloc")]
        internal static extern void* __nix__malloc(long size);

        [DllImport("kernel32.dll", EntryPoint = "HeapFree", SetLastError = false)]
        internal static extern bool __win__free(void* hHeap, uint dwFlags, void* ptr);

        [DllImport("libc.so", EntryPoint = "free")]
        internal static extern void __nix__free(void* ptr);

        [DllImport("kernel32.dll")]
        internal static extern void* HeapCreate(int flOptions, long dwInitialSize, long dwMaximumSize);

        #endregion
#endif

        public const int WORD_SIZE = sizeof(__size);


        internal static Alloc AllocFunc { get; }

        internal static Free FreeFunc { get; }

        public static bool Is64Bit => IntPtr.Size == sizeof(long);

        public static Platform SystemPlatform =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Platform.Windows :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Platform.Linux :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Platform.OSX : Platform.Unknown;

        public static Architecture SystemArchitecture => RuntimeInformation.OSArchitecture;

        
        static SystemInformation()
        {
#if USE_PLATFORM_SPECIFIC_MALLOC
            switch (SystemPlatform)
            {
                case Platform.Windows:
                    (AllocFunc, FreeFunc) = (sz =>
                    {
                        void* heap = HeapCreate(0x00040005, sz, 0);
                        void* ptr = __win__malloc(heap, 0x00000004, sz);

                        __winheaphnds[(long)ptr] = (long)heap;

                        return ptr;
                    }, ptr =>
                    {
                        void* heap = (void*)__winheaphnds[(long)ptr];

                        __win__free(heap, 0x00000000, ptr);
                    });

                    break;
                case Platform.Linux:
                case Platform.OSX:
                    (AllocFunc, FreeFunc) = (__nix__malloc, __nix__free);

                    break;
                case Platform.Unknown:
#endif
            (AllocFunc, FreeFunc) = (sz => (void*)Marshal.AllocHGlobal((int)sz), ptr => Marshal.FreeHGlobal((IntPtr)ptr));
#if USE_PLATFORM_SPECIFIC_MALLOC
                    break;
            }
#endif
        }
    }

#if !DEBUG
    [DebuggerStepThrough, DebuggerNonUserCode]
#endif
    public unsafe sealed class MemoryAllocator
        : IDisposable
    {
        public static readonly int TABLEENTRY_SIZE = sizeof(BlockAllocationData);

        internal readonly __size _size;
        internal byte* _raw;
        
        
        public bool IsDisposed { private set; get; }

        internal BlockAllocationData* DataPtr => (BlockAllocationData*)(_raw + WORD_SIZE);

        internal byte* StartOfAllocations => _raw + WORD_SIZE + (__size)TABLEENTRY_SIZE * *TableSize;

        internal __size* TableSize => (__size*)_raw;

        public __size UsedBytes => UsedDataBytes + (__size)(StartOfAllocations - _raw);

        public __size UsedDataBytes => SafeExec(() =>
        {
            __size sz = 0;

            lock (this)
                for (__size i = 0, c = *TableSize; i < c; ++i)
                    if (DataPtr[c].IsAllocated)
                        sz += DataPtr[c].Size;

            return sz;
        });

        public __size TotalSize => SafeExec(() => _size);

        public __size FreeBytes => TotalSize - UsedBytes;

        public __size AllocatedBlockCount => SafeExec(() =>
        {
            __size cnt = 0;

            lock (this)
                for (int i = 0; i < *TableSize; ++i)
                    if (DataPtr[i].IsAllocated)
                        ++cnt;

            return cnt;
        });


        ~MemoryAllocator() => Dispose();

        public MemoryAllocator(__size size)
        {
            if (size < WORD_SIZE)
                throw new ArgumentException($"The memory allocator must have a minimum capacity of {WORD_SIZE} B.", nameof(size));

            _size = size;
            _raw = (byte*)0;

            try
            {
                _raw = (byte*)AllocFunc(size);
            }
            catch (OverflowException ex)
            {
                throw new OutOfMemoryException($"The memory allocator was unable to allocate {size} Bytes.", ex);
            }

            Parallel.For(0, size, x => _raw[x] = 0x00);
        }

        public void Dispose()
        {
            if (!IsDisposed)
                lock (this)
                {
                    FreeFunc(_raw);

                    IsDisposed = true;
                }
        }

        public MemoryBlock Allocate(__size size) => Allocate(size, true);

        public MemoryBlock Allocate(__size size, bool clear_region) => SafeExec(() =>
        {
            Defragment();

            lock (this)
            {
                void fail() => throw new OutOfMemoryException("The memory allocator has run out of memory. Try freeing up allocated memory blocks or reduce the allocation size.");

                if (FreeBytes < size + TABLEENTRY_SIZE * 2)
                    fail();

                __size id = 0;

                while (id < *TableSize)
                    if (DataPtr[id].IsAllocated)
                        ++id;
                    else
                        break;

                if (id == *TableSize)
                {
                    __size idoffs = (__size)Math.Max(16, 2 * Math.Sqrt(*TableSize + 1));
                    __size szoffs = idoffs * (__size)TABLEENTRY_SIZE;

                    for (__size i = 0; i < *TableSize; ++i)
                    {
                        BlockAllocationData* dat = DataPtr + i;

                        if (dat->IsAllocated)
                            dat->Lock(() => MoveData(dat, dat->Offset + szoffs));
                    }

                    id += idoffs - 1;
                    *TableSize += idoffs;
                }

                __size offs = (__size)(StartOfAllocations - _raw);

                for (__size i = 0; i < *TableSize; ++i)
                    if (DataPtr[i].IsAllocated)
                        offs += DataPtr[i].Size;

                if (offs + size >= FreeBytes)
                    fail();

                BlockAllocationData newdat = new BlockAllocationData
                {
                    State = BlockAllocationState.Allocated,
                    Offset = offs,
                    Size = size,
                };
                DataPtr[id] = newdat;

                if (clear_region)
                    newdat.Lock(() => Parallel.For(0, size, x => _raw[x + offs] = 0x00));

                return new MemoryBlock(this, id);
            }
        });

        public MemoryBlock GetBlock(__size ID)
        {
            void fail() => throw new ArgumentException("An allocated memory block associated with the given ID could not be found.", nameof(ID));

            lock (this)
                if ((ID < 0) || (ID >= *TableSize))
                    fail();
                else if (!DataPtr[ID].IsAllocated)
                    fail();
                else
                    return new MemoryBlock(this, ID);

            return default;
        }

        public void Free(MemoryBlock block) => SafeExec(() =>
        {
            if (block is null)
                throw new ArgumentNullException(nameof(block));
            else
            {
                void fail() => throw new ArgumentException("The given memory block could not be found by the current memory allocator.", nameof(block));
                __size id = block._uuid;

                lock (this)
                    if ((id < 0) || (id >= *TableSize))
                        fail();
                    else
                    {
                        BlockAllocationData* dat = DataPtr + id;

                        if (!dat->IsAllocated)
                            fail();
                        else
                            dat->State = BlockAllocationState.Unallocated;

                        if (id == *TableSize - 1)
                            --*TableSize;
                    }

                Defragment();
            }
        });

        public override string ToString() => SafeExec(() =>
        {
            lock (this)
                return $"{*TableSize} Blocks ({UsedBytes / 1024f:F1} KB / {TotalSize / 1024f:F1} KB, {100f * UsedBytes / TotalSize:F1} %)";
        });

        public void Defragment() => SafeExec(() =>
        {
            lock (this)
                if (*TableSize > 0)
                {
                    __size end = *TableSize - 1;

                    while ((end > 0) && !DataPtr[end].IsAllocated)
                        --end;

                    __size offs = (__size)(StartOfAllocations - _raw);

                    for (__size i = 0; i < end; ++i)
                    {
                        BlockAllocationData* dat = DataPtr + end;

                        if (dat->IsAllocated)
                            dat->Lock(() =>
                            {
                                if (offs != dat->Offset)
                                    MoveData(dat, offs);

                                offs += dat->Size;
                            });
                    }

                    *TableSize = end + 1;
                }
        });

        private void MoveData(BlockAllocationData* dat, __size new_offs) => SafeExec(() =>
        {
            byte* copy = stackalloc byte[(int)dat->Size];

            Parallel.For(0, dat->Size, x => copy[x] = _raw[dat->Offset + x]);
            Parallel.For(0, dat->Size, x => _raw[new_offs + x] = copy[x]);

            dat->Offset = new_offs;
        });

        private void SafeExec(Action f) => SafeExec(() =>
        {
            f();

            return false;
        });

        private T SafeExec<T>(Func<T> f)
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(MemoryAllocator));
            else if (f is null)
                throw new ArgumentNullException(nameof(f));
            else
                return f();
        }
    }

#if !DEBUG
    [DebuggerStepThrough, DebuggerNonUserCode]
#endif
    public unsafe sealed class MemoryBlock
        : IDisposable
    {
        private readonly MemoryAllocator _ma;
        internal readonly __size _uuid;


        public byte this[__size index]
        {
            set => CheckAccess(index, o => _ma._raw[o + index] = value);
            get => CheckAccess(index, o => _ma._raw[o + index]);
        }

        public __size Size => Fetch()->Size;

        public __size Offset => Fetch()->Offset;

        public __size ID => _uuid;

        [Obsolete("DO NOT USE THIS POINTER - IT IS VERY UNSAFE AND IT LEAKS!")]
        public void* VeryUnsafePointer => _ma._raw + Fetch()->Offset;
        

        internal MemoryBlock(MemoryAllocator ma, __size id)
        {
            _uuid = id;
            _ma = ma;
        }

        internal T CheckAccess<T>(__size addr, Func<__size, T> f)
        {
            if (f is null)
                throw new ArgumentNullException(nameof(f));
            else
            {
                BlockAllocationData* dat = Fetch();

                if ((addr < 0) || (addr >= dat->Size))
                    throw new AccessViolationException($"You do not have the required privileges to access the memory address {addr:x8}h.");
                else
                    return dat->Lock(() => f(dat->Offset));
            }
        }

        public void Free() => _ma?.Free(this);

        public byte[] ToBytes() => ToBytes(Size);

        public byte[] ToBytes(__size count) => ToBytes(0, count);

        public byte[] ToBytes(__size offset, __size count)
        {
            BlockAllocationData* dat = Fetch();

            return dat->Lock(() =>
            {
                if ((count < 0) || (count + offset > dat->Size))
                    throw new IndexOutOfRangeException($"The interval [{offset:x8}h..{offset + count:x8}h] is (partially) out of range of the current memory block.");

                byte[] res = new byte[count];

                offset += dat->Offset;

                Parallel.For(0, count, i => res[i] = _ma._raw[i + offset]);

                return res;
            });
        }

        public MemoryStream GetStream() => new MemoryStream(ToBytes());

        public override string ToString() => $"ID: {_uuid:x8}, {*Fetch()}, MEM: {_ma}";

        private BlockAllocationData* Fetch() =>
            _ma?.IsDisposed ?? true ? throw new ObjectDisposedException(nameof(_ma), "The underlying memory allocator has been disposed.") : _ma.DataPtr + _uuid;

        public void Dispose() => Free();


        public static implicit operator MemoryStream(MemoryBlock block) => block.GetStream();

        public static implicit operator byte[](MemoryBlock block) => block.ToBytes();
    }

#if !DEBUG
    [DebuggerStepThrough, DebuggerNonUserCode]
#endif
    [Serializable, StructLayout(LayoutKind.Sequential), NativeCppClass]
    internal struct BlockAllocationData
    {
        public BlockAllocationState State;
        public __size Offset;
        public __size Size;

        public bool IsAllocated => State.HasFlag(BlockAllocationState.Allocated);


        internal void Lock(Action f) => Lock(() =>
        {
            f();

            return false;
        });

        internal T Lock<T>(Func<T> f)
        {
            if (!IsAllocated)
                throw new InvalidOperationException("The current block has not been allocated.");

            T res = default;

            while (State.HasFlag(BlockAllocationState.Locked))
                Thread.Sleep(20);

            State |= BlockAllocationState.Locked;

            try
            {
                if (f != null)
                    res = f();
            }
            finally
            {
                State &= ~BlockAllocationState.Locked;
            }

            return res;
        }

        public override string ToString() => $"[{Offset:x8}h ... {Offset + Size:x8}h] ({Size / 1024f:F1} KB, {State.ToString()})";
    }

    [Serializable, Flags]
    internal enum BlockAllocationState
        : byte
    {
        Unallocated = 0b_0000_0000,
        Allocated   = 0b_0000_0001,
        Locked      = 0b_0000_0010,
    }

    [Serializable]
    public enum Platform
        : byte
    {
        Windows,
        Linux,
        OSX,
        Unknown
    }
}

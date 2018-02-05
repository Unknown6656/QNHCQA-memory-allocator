using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System;

using QNHCQA;

namespace UnitTests
{
    [TestClass]
    public unsafe sealed class UnitTests
    {
        [TestMethod]
        public void Test_00()
        {
            MemoryAllocator mem = new MemoryAllocator(1024);
        }

        [TestMethod, ExpectedException(typeof(ArgumentException))]
        public void Test_01()
        {
            using (MemoryAllocator mem = new MemoryAllocator(SystemInformation.WORD_SIZE - 1))
#pragma warning disable CS0642
                ;
#pragma warning restore CS0642
        }

        [TestMethod, ExpectedException(typeof(OutOfMemoryException))]
        public void Test_02()
        {
            using (MemoryAllocator mem = new MemoryAllocator(SystemInformation.WORD_SIZE))
            {
                MemoryBlock blk = mem.Allocate(0);
            }
        }
        
        [TestMethod, ExpectedException(typeof(OutOfMemoryException))]
        public void Test_03()
        {
            const int sz = 1024;

            using (MemoryAllocator mem = new MemoryAllocator(SystemInformation.WORD_SIZE + sz))
            {
                MemoryBlock blk = mem.Allocate(sz);
            }
        }

        [TestMethod, Timeout(2000), Ignore]
        public void Test_04()
        {
            const int size = 128 * 1024;
            const int blocksize = 1024;

            using (MemoryAllocator mem = new MemoryAllocator(size))
            {
                long count = (size - mem.UsedBytes + mem.UsedDataBytes) / (MemoryAllocator.TABLEENTRY_SIZE + blocksize);

                for (int i = 0; i < count; ++i)
                {
                    mem.Allocate(blocksize);
                }
            }
        }

        [TestMethod]
        public void Test_05()
        {
            const int size = 1024 * 1024;
            const int blocksize = 1024;
            const int count = 32;

            using (MemoryAllocator mem = new MemoryAllocator(size))
            {
                MemoryBlock[] blocks = new MemoryBlock[count];

                for (int i = 0; i < count; ++i)
                    blocks[i] = mem.Allocate(blocksize);

                Assert.AreEqual(count, mem.AllocatedBlockCount);

                foreach (MemoryBlock b in blocks)
                    b.Free();

                Assert.AreEqual(0, mem.AllocatedBlockCount);
            }
        }

        [TestMethod, ExpectedException(typeof(InvalidOperationException))]
        public void Test_06()
        {
            using (MemoryAllocator mem = new MemoryAllocator(2048))
            {
                MemoryBlock block = mem.Allocate(1024, true);

                block.Free();
                block[0] = 0xff;
            }
        }

        [TestMethod]
        public void Test_07()
        {
            using (MemoryAllocator mem = new MemoryAllocator(2048))
            {
                MemoryBlock block = mem.Allocate(1024, true);

                block[0] = 0xff;

                Assert.AreEqual(0xff, block[0]);

                block.Free();
            }
        }

        [TestMethod, ExpectedException(typeof(AccessViolationException))]
        public void Test_08()
        {
            using (MemoryAllocator mem = new MemoryAllocator(2048))
            {
                MemoryBlock block = mem.Allocate(1024, true);

                block[-1] ^= 0xff;
                block.Free();
            }
        }

        [TestMethod, ExpectedException(typeof(AccessViolationException))]
        public void Test_09()
        {
            using (MemoryAllocator mem = new MemoryAllocator(2048))
            {
                const int blocksize = 1024;
                MemoryBlock block = mem.Allocate(blocksize, true);

                block[blocksize] ^= 0xff;
                block.Free();
            }
        }

        [TestMethod]
        public void Test_10()
        {
            using (MemoryAllocator mem = new MemoryAllocator(2048))
            using (MemoryBlock block = mem.Allocate(1024))
            using (MemoryStream s = block)
            {
                Assert.IsTrue(s.CanSeek);
                Assert.IsTrue(s.CanRead);
                Assert.AreEqual(block.Size, s.Length);
            }
        }

        [TestMethod, ExpectedException(typeof(IOException))]
        public void Test_11()
        {
            using (MemoryAllocator mem = new MemoryAllocator(2048))
            using (MemoryBlock block = mem.Allocate(1024, true))
            using (MemoryStream ms = block)
                ms.Seek(-1, SeekOrigin.Begin);
        }

        [TestMethod, ExpectedException(typeof(IndexOutOfRangeException))]
        public void Test_12()
        {
            using (MemoryAllocator mem = new MemoryAllocator(2048))
            using (MemoryBlock block = mem.Allocate(1024))
            {
                byte[] arr = block;

                ++arr[block.Size];
            }
        }

        [TestMethod]
        public void Test_13()
        {
            using (MemoryAllocator mem = new MemoryAllocator(2048))
            {
                MemoryBlock block = mem.Allocate(1024, true);
#pragma warning disable CS0618
                byte* ptr = (byte*)block.VeryUnsafePointer;
#pragma warning restore CS0618

                for (int i = 0; i < block.Size; ++i)
                    Assert.AreEqual(0, ptr[i]);

                char[] str = "Hello World!".ToCharArray();

                for (int i = 0; i < str.Length; ++i)
                    ((char*)ptr)[i] = str[i];


                string extracted = new string((char*)ptr);

                Assert.AreEqual(new string(str), extracted);
            }
        }

        [TestMethod]
        public void Test_14()
        {
            const int blocksize = 1024;

            using (MemoryAllocator mem = new MemoryAllocator(2048))
            using (MemoryBlock block = mem.Allocate(blocksize))
            {
                byte[] arr = block.ToBytes();

                Assert.AreEqual(blocksize, block.Size);
                Assert.AreEqual(blocksize, arr.Length);
            }
        }

        [TestMethod]
        public void Test_15()
        {
            const int blocksize = 1024;

            using (MemoryAllocator mem = new MemoryAllocator(2048))
            using (MemoryBlock block = mem.Allocate(blocksize))
                Assert.AreEqual(mem.UsedBytes - mem.UsedDataBytes, block.Offset);
        }

        [TestMethod]
        public void Test_16()
        {
            const int blocksize = 1024;
            Random rand = new Random();

            using (MemoryAllocator mem = new MemoryAllocator(2048))
            {
                using (MemoryBlock block = mem.Allocate(blocksize, false))
                    for (int i = 0; i < block.Size; ++i)
                        block[i] = (byte)(rand.Next() & 0xff);

                mem.Defragment();

                using (MemoryBlock block = mem.Allocate(blocksize, true))
                    for (int i = 0; i < block.Size; ++i)
                        Assert.AreEqual(block[i], 0x00);
            }
        }

        [TestMethod]
        public void Test_17()
        {
            const int blocksize = 128;
            const int taskcount = 256;

            using (MemoryAllocator mem = new MemoryAllocator(taskcount * 2 * blocksize))
            {
                var fac = new TaskFactory<MemoryBlock>();
                var tasks = from i in Enumerable.Range(0, taskcount)
                            select new
                            {
                                ID = i,
                                Task = fac.StartNew(() => mem.Allocate(blocksize))
                            };

                foreach (var t in tasks)
                {
                    t.Task.Wait();

                    MemoryBlock block = t.Task.Result;

                    Assert.AreEqual(block.Size, blocksize);

                    block.Free();
                }
            }
        }

        [TestMethod]
        public void Test_18()
        {
            const int addr = 0x148;
            const byte val = 0x42;

            using (MemoryAllocator mem = new MemoryAllocator(4096))
            using (MemoryBlock blk = mem.Allocate(1024))
            {
                blk[addr] = val;

                MemoryBlock refcopy = mem.GetBlock(blk.ID);

                Assert.AreEqual(blk.ID, refcopy.ID);
                Assert.AreEqual(blk.Size, refcopy.Size);
                Assert.AreEqual(blk.Offset, refcopy.Offset);
                Assert.AreEqual(val, refcopy[addr]);
            }
        }

        [TestMethod, ExpectedException(typeof(ArgumentException))]
        public void Test_19()
        {
            using (MemoryAllocator mem = new MemoryAllocator(4096))
            using (MemoryBlock blk = mem.Allocate(1024))
            {
                MemoryBlock refcopy = mem.GetBlock(blk.ID == 0 ? 1 : 0);
            }
        }

        [TestMethod, ExpectedException(typeof(ArgumentException))]
        public void Test_20()
        {
            const int addr = 0x148;
            const byte val = 0x42;

            using (MemoryAllocator mem = new MemoryAllocator(4096))
            using (MemoryBlock blk = mem.Allocate(1024))
            {
                blk[addr] = val;

                MemoryBlock refcopy = mem.GetBlock(blk.ID);

                blk.Free();

                byte v = blk[addr];
            }
        }

        [TestMethod, ExpectedException(typeof(ArgumentException))]
        public void Test_21()
        {
            const int addr = 0x148;
            const byte val = 0x42;

            using (MemoryAllocator mem = new MemoryAllocator(4096))
            using (MemoryBlock blk = mem.Allocate(1024))
            {
                blk[addr] = val;

                mem.GetBlock(blk.ID).Dispose();

                byte v = blk[addr];
            }
        }

        [TestMethod, ExpectedException(typeof(OutOfMemoryException))]
        public void Test_22()
        {
            using (MemoryAllocator mem = new MemoryAllocator(long.MaxValue))
#pragma warning disable CS0642
                ;
#pragma warning restore CS0642
        }
    }
}

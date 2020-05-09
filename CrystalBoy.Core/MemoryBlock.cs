﻿#region Copyright Notice
// This file is part of CrystalBoy.
// Copyright © 2008-2011 Fabien Barbier
// 
// CrystalBoy is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// CrystalBoy is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

namespace CrystalBoy.Core
{
	public sealed unsafe class MemoryBlock: IDisposable
	{
		#region Static Members

#if WIN32 && PINVOKE
		private static IntPtr hHeap = NativeMethods.GetProcessHeap();
#endif

		#endregion

#if !(WIN32 && PINVOKE) && GCHANDLE
		private byte[] buffer;
		private GCHandle gcHandle;
#endif
		private void* memoryPointer;
		private int length;
		private bool disposed;

		#region Constructors

		/// <summary>Initializes a new instance of the <see cref="MemoryBlock"/> class.</summary>
		/// <param name="length">The length of the memory block to allocate.</param>
		public MemoryBlock(int length) { Alloc(length); }

		#endregion

		#region Destructors

		/// <summary>Finalizes an instance of the <see cref="MemoryBlock"/> class.</summary>
		~MemoryBlock() { Dispose(false); }

		private void Dispose(bool disposing)
		{
			if (!disposed)
			{
				Free();
				GC.SuppressFinalize(this);
				disposed = true;
			}
		}

		/// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
		public void Dispose()
		{
			Dispose(true);
		}

		#endregion

		#region Memory Management

		private void Alloc(int length)
		{
#if WIN32 && PINVOKE
			void* memoryPointer;

			if (length < 0)
				throw new ArgumentOutOfRangeException("length");

			memoryPointer = NativeMethods.HeapAlloc(hHeap, NativeMethods.HEAP_GENERATE_EXCEPTIONS, (UIntPtr)length);
			if (memoryPointer == (void*)NativeMethods.STATUS_NO_MEMORY)
				throw new OutOfMemoryException();
			else if (memoryPointer == (void*)NativeMethods.STATUS_ACCESS_VIOLATION)
				throw new AccessViolationException();
			else
			{
				this.memoryPointer = memoryPointer;
				this.length = length;
			}
#elif GCHANDLE // This seems like a viable alternative method
			this.buffer = new byte[length];
			GC.Collect(); // Force a GC Collection, hoping it will pack the newly-allocated array together with other data in memory.
			this.gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned); // Once pinned, the buffer will never move.
			this.memoryPointer = gcHandle.AddrOfPinnedObject().ToPointer();
			this.length = length;
#else
			this.memoryPointer = (void*)Marshal.AllocHGlobal(length);
			this.length = length;
#endif
		}

		void Free()
		{
			if (memoryPointer != null)
			{
#if WIN32 && PINVOKE
				NativeMethods.HeapFree(hHeap, 0, this.memoryPointer);
#elif GCHANDLE
				if (this.buffer != null) this.gcHandle.Free();

				this.buffer = null;
#else
				Marshal.FreeHGlobal((IntPtr)this.memoryPointer);
#endif
				this.memoryPointer = null;
				this.length = 0;
			}
		}

		#endregion

		/// <summary>Gets or sets the <see cref="byte"/> value at the specified offset.</summary>
		/// <value>The <see cref="byte"/> value at the specified offset.</value>
		/// <param name="offset">The offset of the value to access.</param>
		/// <returns>The <see cref="byte"/> value at the specified offset.</returns>
		/// <exception cref="System.IndexOutOfRangeException"><paramref name="offset"/> is outside of the allowed range.</exception>
		public byte this[int offset]
		{
			get
			{
				if (offset < 0 || offset > length)
					throw new IndexOutOfRangeException();
				return ((byte*)memoryPointer)[offset];
			}
			set
			{
				if (offset < 0 || offset > length)
					throw new IndexOutOfRangeException();
				((byte*)memoryPointer)[offset] = value;
			}
		}

		/// <summary>Gets the pointer to the memory block.</summary>
		/// <value>The pointer to the memory block.</value>
		public void* Pointer { get { return memoryPointer; } }

		/// <summary>Gets the length of the memory block.</summary>
		/// <value>The length of the memory block.</value>
		public int Length { get { return length; } }

		/// <summary>Gets a value indicating whether this instance is disposed.</summary>
		/// <value><see langword="true" /> if this instance is disposed; otherwise, <see langword="false" />.</value>
		public bool IsDisposed { get { return disposed; } }

		#region Copy

		public static void Copy(MemoryBlock destination, int destinationOffset, MemoryBlock source, int sourceOffset, int length)
		{
			if (destination.memoryPointer == null || source.memoryPointer == null)
				throw new InvalidOperationException();
			if (destinationOffset < 0)
				throw new ArgumentOutOfRangeException("destinationOffset");
			if (sourceOffset < 0)
				throw new ArgumentOutOfRangeException("sourceOffset");
			if (length < 0)
				throw new ArgumentOutOfRangeException("length");

			Memory.Copy((byte*)destination.memoryPointer + destinationOffset, (byte*)source.memoryPointer + sourceOffset, length);
		}

		public static void Copy(IntPtr destination, IntPtr source, int length)
		{
			if (length < 0)
				throw new ArgumentOutOfRangeException("length");

			Memory.Copy((void*)destination, (void*)source, length);
		}

		public static void Copy(void* destination, void* source, int length) { Memory.Copy(destination, source, length); }

		public static void Copy(void* destination, void* source, uint length) { Memory.Copy(destination, source, length); }

		#endregion

		#region Set

		public static void Set(MemoryBlock destination, int destinationOffset, byte value, int length)
		{
			if (destination.memoryPointer == null)
				throw new InvalidOperationException();
			if (destinationOffset < 0)
				throw new ArgumentOutOfRangeException("destinationOffset");
			if (length < 0)
				throw new ArgumentOutOfRangeException("length");

			Memory.Set((byte*)destination.memoryPointer + destinationOffset, value, (uint)length);
		}

		public static void Set(IntPtr destination, byte value, int length)
		{
			if (length < 0)
				throw new ArgumentOutOfRangeException("length");

			Memory.Set((void*)destination, value, (uint)length);
		}

		public static void Set(void* destination, byte value, int length) { Memory.Set(destination, value, length); }

		public static void Set(void* destination, byte value, uint length) { Memory.Set(destination, value, length); }

		#endregion
	}
}

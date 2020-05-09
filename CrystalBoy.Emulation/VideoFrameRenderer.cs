﻿using System;
using CrystalBoy.Core;

namespace CrystalBoy.Emulation
{
    public sealed class VideoFrameRenderer : IDisposable
	{
		private MemoryBlock _renderPaletteMemoryBlock;

        private unsafe uint** _backgroundPalettes32;
        private unsafe uint** _spritePalettes32;
        private unsafe ushort** _backgroundPalettes16;
        private unsafe ushort** _spritePalettes16;

        private uint[] _backgroundPalette;
		private uint[] _objectPalette1;
		private uint[] _objectPalette2;
		
		public unsafe VideoFrameRenderer()
		{
			uint** pointerTable;
			uint* paletteTable;

			// We will allocate memory for 16 palettes of 4 colors each, and for a palette pointer table of 16 pointers
			_renderPaletteMemoryBlock = new MemoryBlock(2 * 8 * sizeof(uint*) + 2 * 8 * 4 * sizeof(uint));

			pointerTable = (uint**)_renderPaletteMemoryBlock.Pointer; // Take 16 uint* at the beginning for pointer table
			paletteTable = (uint*)(pointerTable + 16); // Take the rest for palette array

			// Fill the pointer table with palette
			for (int i = 0; i < 16; i++)
				pointerTable[i] = paletteTable + 4 * i; // Each palette is 4 uint wide

			_backgroundPalettes32 = pointerTable; // First 8 pointers are for the 8 background palettes
			_spritePalettes32 = _backgroundPalettes32 + 8; // Other 8 pointers are for the 8 sprite palettes

			// We'll use the same memory for 16 and 32 bit palettes, because only one will be used at once
			_backgroundPalettes16 = (ushort**)_backgroundPalettes32;
			_spritePalettes16 = _backgroundPalettes16 + 8;

			_backgroundPalette = new uint[4];
			_objectPalette1 = new uint[4];
			_objectPalette2 = new uint[4];
		}

		internal void Reset(bool colorHardware, bool useBootRom)
		{
			if (!colorHardware || useBootRom)
			{
				Buffer.BlockCopy(LookupTables.GrayPalette, 0, _backgroundPalette, 0, 4 * sizeof(uint));
				Buffer.BlockCopy(LookupTables.GrayPalette, 0, _objectPalette1, 0, 4 * sizeof(uint));
				Buffer.BlockCopy(LookupTables.GrayPalette, 0, _objectPalette2, 0, 4 * sizeof(uint));
			}
		}
		
		public void Dispose()
		{
			_renderPaletteMemoryBlock.Dispose();
		}

		public unsafe void RenderVideoBorder32(VideoFrameData frame, IntPtr buffer, int stride)
		{
			DrawBorder32(frame, (byte*)buffer, stride);
		}

		public unsafe void RenderVideoFrame32(VideoFrameData frame, IntPtr buffer, int stride)
		{
			if (frame.VideoMemorySnapshot.SuperGameBoyScreenStatus != 1)
			{
				if (frame.VideoMemorySnapshot.SuperGameBoyScreenStatus == 0 && (frame.VideoMemorySnapshot.LCDC & 0x80) != 0)
				{
					if (frame.IsRunningInColorMode)
					{
						FillPalettes32((ushort*)frame.VideoMemorySnapshot.PaletteMemory);
						DrawColorFrame32(frame, (byte*)buffer, stride);
					}
					else
					{
						if (frame.GreyPaletteUpdated)
						{
							ApplyPaletteMemoryUpdates(frame);
							FillPalettes32((ushort*)frame.VideoMemorySnapshot.PaletteMemory);
							SyncGreyscalePalettes();
						}
						DrawFrame32(frame, (byte*)buffer, stride);
					}
				}
				else
				{
					uint clearColor = frame.VideoMemorySnapshot.SuperGameBoyScreenStatus == 2 ?
						0xFF000000 :
						frame.VideoMemorySnapshot.SuperGameBoyScreenStatus == 3 ?
							LookupTables.StandardColorLookupTable32[/*videoRenderer.ClearColor*/0x7FFF] :
							0xFFFFFFFF;

					ClearBuffer32((byte*)buffer, stride, clearColor);
				}
			}
		}

		private unsafe void ApplyPaletteMemoryUpdates(VideoFrameData frame)
		{
			foreach (var paletteAccess in frame.PaletteAccessList)
			{
				frame.VideoMemorySnapshot.PaletteMemory[paletteAccess.Offset] = paletteAccess.Value;
			}
		}

		private unsafe void SyncGreyscalePalettes()
		{
			for (int i = 0; i < _backgroundPalette.Length; i++)
				_backgroundPalette[i] = _backgroundPalettes32[0][i];
			for (int i = 0; i < _objectPalette1.Length; i++)
				_objectPalette1[i] = _spritePalettes32[0][i];
			for (int i = 0; i < _objectPalette2.Length; i++)
				_objectPalette2[i] = _spritePalettes32[1][i];
		}
		
		private unsafe void FillPalettes16(ushort* paletteData)
		{
			ushort* dest = _backgroundPalettes16[0];

			for (int i = 0; i < 64; i++)
				*dest++ = LookupTables.StandardColorLookupTable16[*paletteData++];
		}

		private unsafe void FillPalettes32(ushort* paletteData)
		{
			uint* dest = _backgroundPalettes32[0];

			for (int i = 0; i < 64; i++)
				*dest++ = LookupTables.StandardColorLookupTable32[*paletteData++];
		}
		
		private unsafe void ClearBuffer16(byte* buffer, int stride, ushort color)
		{
			ushort* bufferPixel;

			for (int i = 0; i < 144; i++)
			{
				bufferPixel = (ushort*)buffer;

				for (int j = 0; j < 160; j++)
					*bufferPixel++ = color;

				buffer += stride;
			}
		}

		private unsafe void ClearBuffer32(byte* buffer, int stride, uint color)
		{
			uint* bufferPixel;

			for (int i = 0; i < 144; i++)
			{
				bufferPixel = (uint*)buffer;

				for (int j = 0; j < 160; j++)
					*bufferPixel++ = color;

				buffer += stride;
			}
		}
		
		private struct ObjectData
		{
			public int Left;
			public int Right;
			public int PixelData;
			public int Palette;
			public bool Priority;
		}

		ObjectData[] _objectData = new ObjectData[10];

		/// <summary>Draws the SGB border into a 32 BPP buffer.</summary>
		/// <param name="frame">The frame for which to draw the border.</param>
		/// <param name="buffer">Destination pixel buffer.</param>
		/// <param name="stride">Buffer line stride.</param>
		private unsafe void DrawBorder32(VideoFrameData frame, byte* buffer, int stride)
		{
			uint[] paletteData = new uint[8 << 4];
			int mapRowOffset = -32;

			// Fill only the 4 border palettes… Just ignore the others
			for (int i = 0x40; i < paletteData.Length; i++) paletteData[i] = LookupTables.StandardColorLookupTable32[frame.SgbBorderMapData[0x400 - 0x40 + i]];

			for (int i = 0; i < 224; i++)
			{
				uint* pixelPointer = (uint*)buffer;
				int tileBaseRowOffset = (i & 0x7) << 1; // Tiles are stored in a weird planar way…
				int mapTileOffset = tileBaseRowOffset != 0 ? mapRowOffset : mapRowOffset += 32;

				for (int j = 32; j-- != 0; mapTileOffset++)
				{
					ushort tileInformation = frame.SgbBorderMapData[mapTileOffset];
					int tileRowOffset = ((tileInformation & 0xFF) << 5) + ((tileInformation & 0x8000) != 0 ? 0xE - tileBaseRowOffset : tileBaseRowOffset);
					int paletteOffset = ((tileInformation >> 10) & 0x7) << 4;

					byte tileValue0 = frame.SgbCharacterData[tileRowOffset];
					byte tileValue1 = frame.SgbCharacterData[tileRowOffset + 1];
					byte tileValue2 = frame.SgbCharacterData[tileRowOffset + 16];
					byte tileValue3 = frame.SgbCharacterData[tileRowOffset + 17];

					if ((tileInformation & 0x4000) != 0)
						for (byte k = 0x01; k != 0; k <<= 1)
						{
							byte color = (tileValue0 & k) != 0 ? (byte)1 : (byte)0;
							if ((tileValue1 & k) != 0) color |= 2;
							if ((tileValue2 & k) != 0) color |= 4;
							if ((tileValue3 & k) != 0) color |= 8;
							*pixelPointer++ = color != 0 ? paletteData[paletteOffset + color] : 0;
						}
					else
						for (byte k = 0x80; k != 0; k >>= 1)
						{
							byte color = (tileValue0 & k) != 0 ? (byte)1 : (byte)0;
							if ((tileValue1 & k) != 0) color |= 2;
							if ((tileValue2 & k) != 0) color |= 4;
							if ((tileValue3 & k) != 0) color |= 8;
							*pixelPointer++ = color != 0 ? paletteData[paletteOffset + color] : 0;
						}
				}

				buffer += stride;
			}
		}

		/// <summary>Draws the current frame into a 32 BPP buffer.</summary>
		/// <param name="frame">The frame to draw.</param>
		/// <param name="buffer">Destination pixel buffer.</param>
		/// <param name="stride">Buffer line stride.</param>
		private unsafe void DrawColorFrame32(VideoFrameData frame, byte* buffer, int stride)
		{
			// WARNING: Very looooooooooong code :D
			// I have to keep track of a lot of variables for this one-pass rendering
			// Since on GBC the priorities between BG, WIN and OBJ can sometimes be weird, I don't think there is a better way of handling this.
			// The code may lack some optimizations tough, but i try my best to keep the variable count the lower possible (taking in account the fact that MS JIT is designed to handle no more than 64 variables...)
			// If you see some possible optimization, feel free to contribute.
			// The code might be very long but it is still very well structured, so with a bit of knowledge on (C)GB hardware you should understand it easily
			// In fact I think the function works pretty much like the real lcd controller on (C)GB... ;)
			byte* bufferLine = buffer;
			uint* bufferPixel;
			int scx, scy, wx, wy;
			int clk, pi, ppi, data1, data2;
			bool bgPriority, tilePriority, winDraw, winDraw2, objDraw, signedIndex;
			byte objDrawn; 
			uint** bgPalettes, objPalettes;
			uint* tilePalette;
			byte* bgMap, winMap,
				bgTile, winTile;
			int bgLineOffset, winLineOffset;
			int bgTileIndex, pixelIndex;
			ushort* bgTiles;
			int i, j;
			int objHeight, objCount;
			uint objColor = 0;

			bgPalettes = this._backgroundPalettes32;
			objPalettes = this._spritePalettes32;

			fixed (ObjectData* objectData = this._objectData)
			fixed (ushort* paletteIndexTable = LookupTables.PaletteLookupTable,
				flippedPaletteIndexTable = LookupTables.FlippedPaletteLookupTable)
			{
				tilePalette = bgPalettes[0];

				data1 = frame.VideoMemorySnapshot.LCDC;
				bgPriority = (data1 & 0x01) != 0;
				bgMap = frame.VideoMemorySnapshot.VideoMemory + ((data1 & 0x08) != 0 ? 0x1C00 : 0x1800);
				winDraw = (data1 & 0x20) != 0;
				winMap = frame.VideoMemorySnapshot.VideoMemory + ((data1 & 0x40) != 0 ? 0x1C00 : 0x1800);
				objDraw = (data1 & 0x02) != 0;
				objHeight = (data1 & 0x04) != 0 ? 16 : 8;
				signedIndex = (data1 & 0x10) == 0;
				bgTiles = (ushort*)(signedIndex ? frame.VideoMemorySnapshot.VideoMemory + 0x1000 : frame.VideoMemorySnapshot.VideoMemory);

				scx = frame.VideoMemorySnapshot.SCX;
				scy = frame.VideoMemorySnapshot.SCY;
				wx = frame.VideoMemorySnapshot.WX - 7;
				wy = frame.VideoMemorySnapshot.WY;

				tilePriority = false;

				clk = 4; // LCD clock
				pi = 0; // Port access list index
				ppi = 0; // Palette access list index

				for (i = 0; i < 144; i++) // Loop on frame lines
				{
					clk += GameBoyMemoryBus.Mode2Duration;

					// Update ports before drawing the line
					while (pi < frame.VideoPortAccessList.Count && frame.VideoPortAccessList[pi].Clock < clk)
					{
						switch (frame.VideoPortAccessList[pi].Port)
						{
							case Port.LCDC:
								data1 = frame.VideoPortAccessList[pi].Value;
								bgPriority = (data1 & 0x01) != 0;
								bgMap = frame.VideoMemorySnapshot.VideoMemory + ((data1 & 0x08) != 0 ? 0x1C00 : 0x1800);
								winDraw = (data1 & 0x20) != 0;
								winMap = frame.VideoMemorySnapshot.VideoMemory + ((data1 & 0x40) != 0 ? 0x1C00 : 0x1800);
								objDraw = (data1 & 0x02) != 0;
								objHeight = (data1 & 0x04) != 0 ? 16 : 8;
								signedIndex = (data1 & 0x10) == 0;
								bgTiles = (ushort*)(signedIndex ? frame.VideoMemorySnapshot.VideoMemory + 0x1000 : frame.VideoMemorySnapshot.VideoMemory);
								break;
							case Port.SCX: scx = frame.VideoPortAccessList[pi].Value; break;
							case Port.SCY: scy = frame.VideoPortAccessList[pi].Value; break;
							case Port.WX: wx = frame.VideoPortAccessList[pi].Value - 7; break;
						}

						pi++;
					}

					// Update palettes before drawing the line (This is necessary for a lot of demos with dynamic palettes)
					while (ppi < frame.PaletteAccessList.Count && frame.PaletteAccessList[ppi].Clock < clk)
					{
						// By doing this, we trash the palette memory snapshot… But at least it works. (Might be necessary to allocate another temporary palette buffer in the future)
						frame.VideoMemorySnapshot.PaletteMemory[frame.PaletteAccessList[ppi].Offset] = frame.PaletteAccessList[ppi].Value;
						bgPalettes[0][frame.PaletteAccessList[ppi].Offset / 2] = LookupTables.StandardColorLookupTable32[((ushort*)frame.VideoMemorySnapshot.PaletteMemory)[frame.PaletteAccessList[ppi].Offset / 2]];

						ppi++;
					}

					// Find valid sprites for the line, limited to 10 like on real GB
					for (j = 0, objCount = 0; j < 40 && objCount < 10; j++) // Loop on OAM data
					{
						bgTile = frame.VideoMemorySnapshot.ObjectAttributeMemory + (j << 2); // Obtain a pointer to the object data

						// First byte is vertical position and that's exactly what we want to compare :)
						data1 = *bgTile - 16;
						if (data1 <= i && data1 + objHeight > i) // Check that the sprite is drawn on the current line
						{
							// Initialize the object data according to what we want
							data2 = bgTile[1]; // Second byte is the horizontal position, we store it somewhere
							objectData[objCount].Left = data2 - 8;
							objectData[objCount].Right = data2;
							data2 = bgTile[3]; // Fourth byte contain flags that we'll examine
							objectData[objCount].Palette = data2 & 0x7; // Use the palette index stored in flags
							objectData[objCount].Priority = (data2 & 0x80) == 0; // Store the priority information
							// Now we check the Y flip flag, as we'll use it to calculate the tile line offset
							if ((data2 & 0x40) != 0)
								data1 = (objHeight + data1 - i - 1) << 1;
							else
								data1 = (i - data1) << 1;
							// Now that we have the line offset, we add to it the tile offset
							if (objHeight == 16) // Depending on the sprite size we'll have to mask bit 0 of the tile index
								data1 += (bgTile[2] & 0xFE) << 4; // Third byte is the tile index
							else
								data1 += bgTile[2] << 4; // A tile is 16 bytes wide
							// Now all that is left is to fetch the tile data :)
							if ((data2 & 0x8) != 0)
								bgTile = frame.VideoMemorySnapshot.VideoMemory + data1 + 0x2000; // Calculate the full tile line address for VRAM Bank 1
							else
								bgTile = frame.VideoMemorySnapshot.VideoMemory + data1; // Calculate the full tile line address for VRAM Bank 0
							// Depending on the X flip flag, we will load the flipped pixel data or the regular one
							if ((data2 & 0x20) != 0)
								objectData[objCount].PixelData = flippedPaletteIndexTable[*(ushort*)bgTile];
							else
								objectData[objCount].PixelData = paletteIndexTable[*(ushort*)bgTile];
							objCount++; // Increment the object counter
						}
					}

					// Initialize the background and window with new parameters
					bgTileIndex = scx >> 3;
					pixelIndex = scx & 7;
					data1 = (scy + i) >> 3; // Background Line Index
					bgLineOffset = (scy + i) & 7;
					if (data1 >= 32) // Tile the background vertically
						data1 -= 32;
					bgTile = bgMap + (data1 << 5) + bgTileIndex;
					winTile = winMap + (((i - wy) << 2) & ~0x1F); // Optimisation for 32 * x / 8 => >> 3 << 5
					winLineOffset = (i - wy) & 7;

					winDraw2 = winDraw && i >= wy;
					
					// Adjust the current pixel to the current line
					bufferPixel = (uint*)bufferLine;

					// Do the actual drawing
					for (j = 0; j < 160; j++) // Loop on line pixels
					{
						clk++;

						// Update palettes before drawing the line (This is necessary for a lot of demos with dynamic palettes)
						while (ppi < frame.PaletteAccessList.Count && frame.PaletteAccessList[ppi].Clock < clk)
						{
							// By doing this, we trash the palette memory snapshot… But at least it works. (Might be necessary to allocate another temporary palette buffer in the future)
							frame.VideoMemorySnapshot.PaletteMemory[frame.PaletteAccessList[ppi].Offset] = frame.PaletteAccessList[ppi].Value;
							bgPalettes[0][frame.PaletteAccessList[ppi].Offset / 2] = LookupTables.StandardColorLookupTable32[((ushort*)frame.VideoMemorySnapshot.PaletteMemory)[frame.PaletteAccessList[ppi].Offset / 2]];

							ppi++;
						}

						objDrawn = 0; // Draw no object by default

						if (objDraw && objCount > 0)
						{
							for (data2 = 0; data2 < objCount; data2++)
							{
								if (objectData[data2].Left <= j && objectData[data2].Right > j)
								{
									objColor = (uint)(objectData[data2].PixelData >> ((j - objectData[data2].Left) << 1)) & 3;
									if ((objDrawn = (byte)(objColor != 0 ? !bgPriority || objectData[data2].Priority ? 2 : 1 : 0)) != 0)
									{
										objColor = objPalettes[objectData[data2].Palette][objColor];
										break;
									}
								}
							}
						}
						if (winDraw2 && j >= wx)
						{
							if (pixelIndex >= 8 || j == 0 || j == wx)
							{
								data2 = *(winTile + 0x2000);
								tilePalette = bgPalettes[data2 & 0x7];
								data1 = ((data2 & 0x40) != 0 ? 7 - winLineOffset : winLineOffset) + (signedIndex ? (sbyte)*winTile++ << 3 : *winTile++ << 3);
								if ((data2 & 0x8) != 0) data1 += 0x1000;
								data1 = (data2 & 0x20) != 0 ? flippedPaletteIndexTable[bgTiles[data1]] : paletteIndexTable[bgTiles[data1]];

								tilePriority = bgPriority && (data2 & 0x80) != 0;

								if (j == 0 && wx < 0)
								{
									pixelIndex = -wx;
									data1 >>= pixelIndex << 1;
								}
								else pixelIndex = 0;
							}

							*bufferPixel++ = objDrawn != 0 && (!(tilePriority || objDrawn == 1) || (data1 & 0x3) == 0) ? objColor : tilePalette[data1 & 0x3];

							data1 >>= 2;
							pixelIndex++;
						}
						else
						{
							if (pixelIndex >= 8 || j == 0)
							{
								if (bgTileIndex++ >= 32) // Tile the background horizontally
								{
									bgTile -= 32;
									bgTileIndex = 0;
								}

								data2 = *(bgTile + 0x2000);
								tilePalette = bgPalettes[data2 & 0x7];
								data1 = ((data2 & 0x40) != 0 ? 7 - bgLineOffset : bgLineOffset) + (signedIndex ? (sbyte)*bgTile++ << 3 : *bgTile++ << 3);
								if ((data2 & 0x8) != 0) data1 += 0x1000;
								data1 = (data2 & 0x20) != 0 ? flippedPaletteIndexTable[bgTiles[data1]] : paletteIndexTable[bgTiles[data1]];

								tilePriority = bgPriority && (data2 & 0x80) != 0;

								if (j == 0 && pixelIndex > 0) data1 >>= pixelIndex << 1;
								else pixelIndex = 0;
							}

							*bufferPixel++ = objDrawn != 0 && (!(tilePriority || objDrawn == 1) || (data1 & 0x3) == 0) ? objColor : tilePalette[data1 & 0x3];
							data1 >>= 2;
							pixelIndex++;
						}
					}

					clk += 216;

					bufferLine += stride;
				}
			}
		}
		
		/// <summary>Draws the current frame into a 32 BPP buffer.</summary>
		/// <param name="frame">The frame to render.</param>
		/// <param name="buffer">The destination buffer.</param>
		/// <param name="stride">The stride of a buffer line.</param>
		private unsafe void DrawFrame32(VideoFrameData frame, byte* buffer, int stride)
		{
			// WARNING: Very looooooooooong code :D
			// I have to keep track of a lot of variables for this one-pass rendering
			// Since on GBC the priorities between BG, WIN and OBJ can sometimes be weird, I don't think there is a better way of handling this.
			// The code may lack some optimizations tough, but i try my best to keep the variable count the lower possible (taking in account the fact that MS JIT is designed to handle no more than 64 variables...)
			// If you see some possible optimization, feel free to contribute.
			// The code might be very long but it is still very well structured, so with a bit of knowledge on (C)GB hardware you should understand it easily
			// In fact I think the function works pretty much like the real lcd controller on (C)GB... ;)
			byte* bufferLine = buffer;
			uint* bufferPixel;
			int scx, scy, wx, wy;
			int clk, pi, data1, data2;
			bool bgDraw, winDraw, winDraw2, objDraw, signedIndex;
			byte objDrawn;
			uint** bgPalettes, objPalettes;
			uint* tilePalette;
			byte* bgMap, winMap,
				bgTile, winTile;
			int bgLineOffset, winLineOffset;
			int bgTileIndex, pixelIndex;
			ushort* bgTiles;
			int i, j;
			int objHeight, objCount;
			uint objColor = 0;

			bgPalettes = this._backgroundPalettes32;
			objPalettes = this._spritePalettes32;

			fixed (ObjectData* objectData = this._objectData)
			fixed (ushort* paletteIndexTable = LookupTables.PaletteLookupTable,
				flippedPaletteIndexTable = LookupTables.FlippedPaletteLookupTable)
			fixed (uint* backgroundPalette = this._backgroundPalette, objectPalette1 = this._objectPalette1, objectPalette2 = this._objectPalette2)
			{
				tilePalette = bgPalettes[0];

				data1 = frame.VideoMemorySnapshot.LCDC;
				bgDraw = (data1 & 0x01) != 0;
				bgMap = frame.VideoMemorySnapshot.VideoMemory + ((data1 & 0x08) != 0 ? 0x1C00 : 0x1800);
				winDraw = (data1 & 0x20) != 0;
				winMap = frame.VideoMemorySnapshot.VideoMemory + ((data1 & 0x40) != 0 ? 0x1C00 : 0x1800);
				objDraw = (data1 & 0x02) != 0;
				objHeight = (data1 & 0x04) != 0 ? 16 : 8;
				signedIndex = (data1 & 0x10) == 0;
				bgTiles = (ushort*)(signedIndex ? frame.VideoMemorySnapshot.VideoMemory + 0x1000 : frame.VideoMemorySnapshot.VideoMemory);

				scx = frame.VideoMemorySnapshot.SCX;
				scy = frame.VideoMemorySnapshot.SCY;
				wx = frame.VideoMemorySnapshot.WX - 7;
				wy = frame.VideoMemorySnapshot.WY;

				UpdatePalette(frame.VideoMemorySnapshot.BGP, tilePalette, backgroundPalette);
				UpdatePalette(frame.VideoMemorySnapshot.OBP0, objPalettes[0], objectPalette1);
				UpdatePalette(frame.VideoMemorySnapshot.OBP1, objPalettes[1], objectPalette2);

				// Initialize the clock, and the port access index.
				clk = 4; // Be off by 4 clock cycles (the minimum time required by a DMG CPU instruction) to compensate write cycle imprecision.
				pi = 0;

				for (i = 0; i < 144; i++) // Loop on frame lines
				{
					// Update ports before drawing the line
					while (pi < frame.VideoPortAccessList.Count && frame.VideoPortAccessList[pi].Clock < clk)
					{
						data2 = frame.VideoPortAccessList[pi].Value;

						switch (frame.VideoPortAccessList[pi].Port)
						{
							case Port.LCDC:
								bgDraw = (data2 & 0x01) != 0;
								bgMap = frame.VideoMemorySnapshot.VideoMemory + ((data2 & 0x08) != 0 ? 0x1C00 : 0x1800);
								winDraw = (data2 & 0x20) != 0;
								winMap = frame.VideoMemorySnapshot.VideoMemory + ((data2 & 0x40) != 0 ? 0x1C00 : 0x1800);
								objDraw = (data2 & 0x02) != 0;
								objHeight = (data2 & 0x04) != 0 ? 16 : 8;
								signedIndex = (data2 & 0x10) == 0;
								bgTiles = (ushort*)(signedIndex ? frame.VideoMemorySnapshot.VideoMemory + 0x1000 : frame.VideoMemorySnapshot.VideoMemory);
								break;
							case Port.SCX: scx = data2; break;
							case Port.SCY: scy = data2; break;
							case Port.WX: wx = data2 - 7; break;
							case Port.BGP:
								UpdatePalette(data2, tilePalette, backgroundPalette);
								break;
							case Port.OBP0:
								UpdatePalette(data2, objPalettes[0], objectPalette1);
								break;
							case Port.OBP1:
								UpdatePalette(data2, objPalettes[1], objectPalette2);
								break;
						}

						pi++;
					}

					// Find valid sprites for the line, limited to 10 like on real GB
					for (j = 0, objCount = 0; j < 40 && objCount < 10; j++) // Loop on OAM data
					{
						bgTile = frame.VideoMemorySnapshot.ObjectAttributeMemory + (j << 2); // Obtain a pointer to the object data

						// First byte is vertical position and that's exactly what we want to compare :)
						data1 = *bgTile - 16;
						if (data1 <= i && data1 + objHeight > i) // Check that the sprite is drawn on the current line
						{
							// Initialize the object data according to what we want
							data2 = bgTile[1]; // Second byte is the horizontal position, we store it somewhere
							objectData[objCount].Left = data2 - 8;
							objectData[objCount].Right = data2;
							data2 = bgTile[3]; // Fourth byte contain flags that we'll examine
							objectData[objCount].Palette = (data2 & 0x10) != 0 ? 1 : 0; // Set the palette index according to the flags
							objectData[objCount].Priority = (data2 & 0x80) == 0; // Store the priority information
																				 // Now we check the Y flip flag, as we'll use it to calculate the tile line offset
							if ((data2 & 0x40) != 0)
								data1 = (objHeight + data1 - i - 1) << 1;
							else
								data1 = (i - data1) << 1;
							// Now that we have the line offset, we add to it the tile offset
							if (objHeight == 16) // Depending on the sprite size we'll have to mask bit 0 of the tile index
								data1 += (bgTile[2] & 0xFE) << 4; // Third byte is the tile index
							else
								data1 += bgTile[2] << 4; // A tile is 16 bytes wide
														 // No all that is left is to fetch the tile data :)
							bgTile = frame.VideoMemorySnapshot.VideoMemory + data1; // Calculate the full tile line address for VRAM Bank 0
																					// Depending on the X flip flag, we will load the flipped pixel data or the regular one
							if ((data2 & 0x20) != 0)
								objectData[objCount].PixelData = flippedPaletteIndexTable[*(ushort*)bgTile];
							else
								objectData[objCount].PixelData = paletteIndexTable[*(ushort*)bgTile];
							objCount++; // Increment the object counter
						}
					}

					// Initialize the background and window with new parameters
					bgTileIndex = scx >> 3;
					pixelIndex = scx & 7;
					data1 = (scy + i) >> 3; // Background Line Index
					bgLineOffset = (scy + i) & 7;
					if (data1 >= 32) // Tile the background vertically
						data1 -= 32;
					bgTile = bgMap + (data1 << 5) + bgTileIndex;
					winTile = winMap + (((i - wy) << 2) & ~0x1F);
					winLineOffset = (i - wy) & 7;

					winDraw2 = winDraw && i >= wy;

					// Adjust the current pixel to the current line
					bufferPixel = (uint*)bufferLine;

					clk += GameBoyMemoryBus.Mode2Duration; // Increment the clock by the duration of mode 2. (This is an approximation…)

					// Do the actual drawing
					for (j = 0; j < 160; j++) // Loop on line pixels
					{
						clk++; // The clock will be incremented by 1 for every pixel, 160 in total. Together with the hardcoded mode 2, this will put the clock to 240 in the end.
						
						// Update ports before drawing the line
						while (pi < frame.VideoPortAccessList.Count && frame.VideoPortAccessList[pi].Clock < clk)
						{
							data2 = frame.VideoPortAccessList[pi].Value;

							switch (frame.VideoPortAccessList[pi].Port)
							{
								case Port.LCDC:
									bgDraw = (data2 & 0x01) != 0;
									bgMap = frame.VideoMemorySnapshot.VideoMemory + ((data2 & 0x08) != 0 ? 0x1C00 : 0x1800);
									winDraw = (data2 & 0x20) != 0;
									winMap = frame.VideoMemorySnapshot.VideoMemory + ((data2 & 0x40) != 0 ? 0x1C00 : 0x1800);
									objDraw = (data2 & 0x02) != 0;
									objHeight = (data2 & 0x04) != 0 ? 16 : 8;
									signedIndex = (data2 & 0x10) == 0;
									bgTiles = (ushort*)(signedIndex ? frame.VideoMemorySnapshot.VideoMemory + 0x1000 : frame.VideoMemorySnapshot.VideoMemory);
									break;
								case Port.SCX: scx = data2; break;
								case Port.SCY: scy = data2; break;
								case Port.WX: wx = data2 - 7; break;
								case Port.BGP:
									UpdatePalette(data2, tilePalette, backgroundPalette);
									break;
								case Port.OBP0:
									UpdatePalette(data2, objPalettes[0], objectPalette1);
									break;
								case Port.OBP1:
									UpdatePalette(data2, objPalettes[1], objectPalette2);
									break;
							}

							pi++;
						}

						objDrawn = 0; // Draw no object by default

						if (objDraw && objCount > 0)
						{
							for (data2 = 0; data2 < objCount; data2++)
							{
								if (objectData[data2].Left <= j && objectData[data2].Right > j)
								{
									objColor = (uint)(objectData[data2].PixelData >> ((j - objectData[data2].Left) << 1)) & 3;
									if ((objDrawn = (byte)(objColor != 0 ? objectData[data2].Priority ? 2 : 1 : 0)) != 0)
									{
										objColor = objPalettes[objectData[data2].Palette][objColor];
										break;
									}
								}
							}
						}
						if (winDraw2 && j >= wx)
						{
							if (pixelIndex >= 8 || j == 0 || j == wx)
							{
								data1 = winLineOffset + (signedIndex ? (sbyte)*winTile++ << 3 : *winTile++ << 3);

								data1 = paletteIndexTable[bgTiles[data1]];

								if (j == 0 && wx < 0)
								{
									pixelIndex = -wx;
									data1 >>= pixelIndex << 1;
								}
								else pixelIndex = 0;
							}

							*bufferPixel++ = objDrawn != 0 && (objDrawn == 2 || (data1 & 0x3) == 0) ? objColor : tilePalette[data1 & 0x3];

							data1 >>= 2;
							pixelIndex++;
						}
						else if (bgDraw)
						{
							if (pixelIndex >= 8 || j == 0)
							{
								if (bgTileIndex++ >= 32) // Tile the background horizontally
								{
									bgTile -= 32;
									bgTileIndex = 0;
								}

								data1 = bgLineOffset + (signedIndex ? (sbyte)*bgTile++ << 3 : *bgTile++ << 3);

								data1 = paletteIndexTable[bgTiles[data1]];

								if (j == 0 && pixelIndex > 0) data1 >>= pixelIndex << 1;
								else pixelIndex = 0;
							}

							*bufferPixel++ = objDrawn != 0 && (objDrawn == 2 || (data1 & 0x3) == 0) ? objColor : tilePalette[data1 & 0x3];
							data1 >>= 2;
							pixelIndex++;
						}
						else *bufferPixel++ = objDrawn != 0 ? objColor : LookupTables.GrayPalette[0];
					}

					clk += 216;

					bufferLine += stride;
				}
			}
		}

		private static unsafe void UpdatePalette(int registerData, uint* destinationPalette, uint* sourceColorPalette)
		{
			*destinationPalette = sourceColorPalette[registerData & 3];
			*++destinationPalette = sourceColorPalette[(registerData >>= 2) & 3];
			*++destinationPalette = sourceColorPalette[(registerData >>= 2) & 3];
			*++destinationPalette = sourceColorPalette[(registerData >> 2) & 3];
		}
	}
}

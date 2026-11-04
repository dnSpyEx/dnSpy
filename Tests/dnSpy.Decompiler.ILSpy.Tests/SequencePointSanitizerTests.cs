/*
    Copyright (C) 2026 dnSpy contributors

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.DebugInfo;
using Xunit;
using UpstreamSequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// The repair of invalid upstream sequence points must never change valid ones, so it keeps working (as a
	/// no-op) when upstream fixes its bugs
	/// </summary>
	public class SequencePointSanitizerTests {
		// Instructions at 0, 1, 2, 7 (5 byte instruction at 2), 12; code size 13
		static readonly uint[] boundaries = { 0, 1, 2, 7, 12, 13 };

		// Line N, column C -> offset N * 100 + C
		static int ToOffset(int line, int column) => line * 100 + column;

		static UpstreamSequencePoint Point(int offset, int endOffset, int line, int startColumn = 1, int endColumn = 10) =>
			new UpstreamSequencePoint { Offset = offset, EndOffset = endOffset, StartLine = line, StartColumn = startColumn, EndLine = line, EndColumn = endColumn };

		static UpstreamSequencePoint Hidden(int offset, int endOffset) {
			var sp = Point(offset, endOffset, 0xfeefee);
			sp.EndLine = 0xfeefee;
			return sp;
		}

		[Fact]
		public void ValidSequencePoints_PassThroughUnchanged() {
			var points = new[] { Point(0, 2, 1), Hidden(2, 7), Point(7, 12, 2), Point(12, 13, 3, 5, 6) };
			var repairs = new List<string>();
			var result = SequencePointSanitizer.Sanitize(points, boundaries, ToOffset, "M", repairs);

			Assert.Empty(repairs);
			Assert.Equal(new[] {
				new SourceStatement(ILSpan.FromBounds(0, 2), TextSpan.FromBounds(101, 110)),
				new SourceStatement(ILSpan.FromBounds(7, 12), TextSpan.FromBounds(201, 210)),
				new SourceStatement(ILSpan.FromBounds(12, 13), TextSpan.FromBounds(305, 306)),
			}, result);
		}

		[Fact]
		public void WithoutABody_NothingIsChecked() {
			var points = new[] { Point(3, 5, 1) };
			var repairs = new List<string>();
			var result = SequencePointSanitizer.Sanitize(points, null, ToOffset, "M", repairs);

			Assert.Empty(repairs);
			Assert.Equal(ILSpan.FromBounds(3, 5), Assert.Single(result).ILSpan);
		}

		[Fact]
		public void StartInsideAnInstruction_IsDropped() {
			var repairs = new List<string>();
			var result = SequencePointSanitizer.Sanitize(new[] { Point(0, 2, 1), Point(3, 7, 2) }, boundaries, ToOffset, "M", repairs);

			Assert.Equal(ILSpan.FromBounds(0, 2), Assert.Single(result).ILSpan);
			Assert.Contains("starts inside an instruction", Assert.Single(repairs));
		}

		[Fact]
		public void EndInsideAnInstruction_IsMovedToTheNextInstruction() {
			var repairs = new List<string>();
			var result = SequencePointSanitizer.Sanitize(new[] { Point(2, 3, 1) }, boundaries, ToOffset, "M", repairs);

			Assert.Equal(ILSpan.FromBounds(2, 7), Assert.Single(result).ILSpan);
			Assert.Single(repairs);
		}

		[Fact]
		public void OffsetsBeyondTheCode_AreClampedOrDropped() {
			var repairs = new List<string>();
			var result = SequencePointSanitizer.Sanitize(new[] { Point(12, 20, 1), Point(13, 20, 2) }, boundaries, ToOffset, "M", repairs);

			Assert.Equal(ILSpan.FromBounds(12, 13), Assert.Single(result).ILSpan);
			Assert.Equal(2, repairs.Count);
		}

		[Fact]
		public void NoTextLocation_IsDropped() {
			var repairs = new List<string>();
			var noLocation = new UpstreamSequencePoint { Offset = 0, EndOffset = 2 };
			var emptyText = Point(2, 7, 1, startColumn: 5, endColumn: 5);
			var result = SequencePointSanitizer.Sanitize(new[] { noLocation, emptyText }, boundaries, ToOffset, "M", repairs);

			Assert.Empty(result);
			Assert.Equal(2, repairs.Count);
		}
	}
}

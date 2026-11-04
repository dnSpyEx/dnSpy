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

using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Contracts.Decompiler;
using UpstreamSequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace dnSpy.Decompiler.ILSpy.Core.DebugInfo {
	/// <summary>
	/// Turns upstream sequence points into dnSpy statements. Valid sequence points are passed through unchanged;
	/// only invalid ones are dropped or fixed, so this is a no-op once upstream stops producing them.
	/// </summary>
	/// <remarks>
	/// ICSharpCode.Decompiler 11.2 sometimes returns IL offsets inside an instruction (e.g. in async MoveNext
	/// methods), and sometimes sequence points for nodes that weren't written (no text location).
	/// </remarks>
	static class SequencePointSanitizer {
		/// <param name="points">Upstream sequence points of one method</param>
		/// <param name="instructionBoundaries">Offsets of all instructions plus the code size, or null if the method
		/// body isn't available (then IL offsets can't be checked)</param>
		/// <param name="toTextOffset">Converts a 1-based (line, column) to an offset in the decompiled text</param>
		/// <param name="methodName">Name used in <paramref name="repairs"/></param>
		/// <param name="repairs">Gets a description of each sequence point that was dropped or fixed</param>
		public static List<SourceStatement> Sanitize(IEnumerable<UpstreamSequencePoint> points, IReadOnlyCollection<uint>? instructionBoundaries,
			Func<int, int, int> toTextOffset, string methodName, List<string> repairs) {
			var boundaries = instructionBoundaries as ISet<uint> ?? (instructionBoundaries is null ? null : new HashSet<uint>(instructionBoundaries));
			uint codeSize = instructionBoundaries is null || instructionBoundaries.Count == 0 ? uint.MaxValue : instructionBoundaries.Max();
			var result = new List<SourceStatement>();
			foreach (var sp in points) {
				if (sp.IsHidden || sp.EndOffset <= sp.Offset)
					continue;
				if (sp.StartLine <= 0) {
					repairs.Add($"{methodName}: dropped statement IL_{sp.Offset:X4}-IL_{sp.EndOffset:X4} (no text location)");
					continue;
				}
				int start = toTextOffset(sp.StartLine, sp.StartColumn);
				int end = toTextOffset(sp.EndLine, sp.EndColumn);
				if (end <= start) {
					repairs.Add($"{methodName}: dropped statement IL_{sp.Offset:X4}-IL_{sp.EndOffset:X4} (empty text span)");
					continue;
				}
				uint ilStart = (uint)sp.Offset;
				uint ilEnd = (uint)sp.EndOffset;
				if (boundaries is not null) {
					// A statement that starts inside an instruction is attributed to the wrong IL: drop it rather than
					// let a breakpoint on it stop in other code. The stepper then treats that IL as hidden.
					if (!boundaries.Contains(ilStart) || ilStart >= codeSize) {
						repairs.Add($"{methodName}: dropped statement IL_{ilStart:X4}-IL_{ilEnd:X4} (starts inside an instruction)");
						continue;
					}
					if (!boundaries.Contains(ilEnd)) {
						uint fixedEnd = ilEnd > codeSize ? codeSize : boundaries.Where(b => b > ilEnd).DefaultIfEmpty(codeSize).Min();
						repairs.Add($"{methodName}: statement IL_{ilStart:X4}-IL_{ilEnd:X4} ends inside an instruction, end moved to IL_{fixedEnd:X4}");
						ilEnd = fixedEnd;
					}
				}
				result.Add(new SourceStatement(ILSpan.FromBounds(ilStart, ilEnd), TextSpan.FromBounds(start, end)));
			}
			return result;
		}
	}
}

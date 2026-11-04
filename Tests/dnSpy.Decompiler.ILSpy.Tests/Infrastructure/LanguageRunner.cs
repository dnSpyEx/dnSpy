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
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Core.Settings;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	/// <summary>
	/// Runs the production decompiler (the <see cref="IDecompiler"/> dnSpy uses) like dnSpy's document tabs do
	/// </summary>
	static class LanguageRunner {
		public static UpstreamCSharpLanguage Create(Action<UpstreamDecompilerSettings>? configure = null) {
			var settings = new UpstreamDecompilerSettings();
			configure?.Invoke(settings);
			return new UpstreamCSharpLanguage(settings);
		}

		public static DecompilationContext CreateContext(Func<MethodDef, bool>? isBodyModified = null) =>
			new DecompilationContext {
				CalculateILSpans = true,
				IsBodyModified = isBodyModified ?? (_ => false),
			};

		public static CapturingDecompilerOutput Decompile(IDecompiler decompiler, IMemberDef member, DecompilationContext? ctx = null, CapturingDecompilerOutput? output = null) {
			output ??= new CapturingDecompilerOutput();
			ctx ??= CreateContext();
			switch (member) {
			case TypeDef type:
				decompiler.Decompile(type, output, ctx);
				break;
			case MethodDef method:
				decompiler.Decompile(method, output, ctx);
				break;
			case FieldDef field:
				decompiler.Decompile(field, output, ctx);
				break;
			case PropertyDef property:
				decompiler.Decompile(property, output, ctx);
				break;
			case EventDef ev:
				decompiler.Decompile(ev, output, ctx);
				break;
			default:
				throw new ArgumentException(member.ToString());
			}
			return output;
		}

		public static (string text, IReadOnlyList<MethodDebugInfo> infos) DecompileType(IDecompiler decompiler, TypeDef type) {
			var output = Decompile(decompiler, type);
			return (output.Text, output.DebugInfos.ToArray());
		}
	}
}

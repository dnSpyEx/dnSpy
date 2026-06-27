/*
    Copyright (C) 2024 mitch.capper+dns@gmail.com

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
using System.Threading;
using dnlib.DotNet;
using dnSpy.Analyzer.Properties;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.Analyzer.TreeNodes {
	sealed class SubtypesNode : SearchNode {
		readonly TypeDef analyzedType;

		public SubtypesNode(TypeDef analyzedType) =>
			this.analyzedType = analyzedType ?? throw new ArgumentNullException(nameof(analyzedType));

		protected override void Write(ITextColorWriter output, IDecompiler decompiler) =>
			output.Write(BoxedTextColor.Text, dnSpy_Analyzer_Resources.SubtypesTreeNode);

		protected override IEnumerable<AnalyzerTreeNodeData> FetchChildren(CancellationToken ct) {
			var analyzer = new ScopedWhereUsedAnalyzer<AnalyzerTreeNodeData>(Context.DocumentService, analyzedType, FindReferencesInType);
			return analyzer.PerformAnalysis(ct);
		}

		IEnumerable<AnalyzerTreeNodeData> FindReferencesInType(TypeDef type) {
			var currentType = type;
			if (analyzedType.IsInterface) {
				while (currentType is not null) {
					for (int i = 0; i < currentType.Interfaces.Count; i++) {
						if (IsEqual(currentType.Interfaces[i].Interface)) {
							yield return new TypeNode(type) { Context = Context };
							break;
						}
					}

					currentType = currentType.BaseType.ResolveTypeDef();
				}
				yield break;
			}

			if (!type.IsClass)
				yield break;

			while (currentType is not null) {
				if (IsEqual(currentType.BaseType))
					yield return new TypeNode(type) { Context = Context };

				currentType = currentType.BaseType.ResolveTypeDef();
			}
		}

		bool IsEqual(ITypeDefOrRef itm) => CheckEquals(analyzedType, itm.GetScopeType());

		public static bool CanShow(TypeDef type) => !type.IsSealed && !type.IsValueType;
	}
}

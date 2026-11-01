// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Analyzer.Properties;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.Analyzer.TreeNodes {
	sealed class MethodImplementsInterfaceNode : SearchNode {
		readonly MethodDef analyzedMethod;

		public MethodImplementsInterfaceNode(MethodDef analyzedMethod) => this.analyzedMethod = analyzedMethod ?? throw new ArgumentNullException(nameof(analyzedMethod));

		protected override void Write(ITextColorWriter output, IDecompiler decompiler) =>
			output.Write(BoxedTextColor.Text, dnSpy_Analyzer_Resources.ImplementsTreeNode);

		protected override IEnumerable<AnalyzerTreeNodeData> FetchChildren(CancellationToken ct) {
			bool includeAllModules = true;
			// explicitly implemented interface methods are private -> ForcePublic required
			var options = analyzedMethod.HasOverrides ? ScopedWhereUsedAnalyzerOptions.ForcePublic : ScopedWhereUsedAnalyzerOptions.None;
			if (includeAllModules)
				options |= ScopedWhereUsedAnalyzerOptions.IncludeAllModules;
			var analyzer = new ScopedWhereUsedAnalyzer<AnalyzerTreeNodeData>(Context.DocumentService, analyzedMethod, FindReferencesInType, options);
			return analyzer.PerformAnalysis(ct);
		}

		// logic for COM interfaces not implemented yet
		IEnumerable<AnalyzerTreeNodeData> FindReferencesInType(TypeDef type) {
			if (!type.IsInterface)
				yield break;

			if (analyzedMethod is { IsVirtual: false, IsStatic: false } || analyzedMethod.IsAbstract) {
				yield break;
			}

			// explicit interface implementation
			if (analyzedMethod.HasOverrides) {
				foreach (var interfaceMethod in type.Methods) {
					if (!analyzedMethod.Overrides.Any(x => CheckOverride(x, interfaceMethod))) continue;
					yield return new MethodNode(interfaceMethod) { Context = Context };
					yield break;
				}
			}

			// implicit interface implementation
			var implementedInterfaceRef = InterfaceMethodImplementedByNode.GetInterface(analyzedMethod.DeclaringType, type);
			if (implementedInterfaceRef is not null) {
				foreach (var method in type.Methods) {
					if (method.Name != analyzedMethod.Name)
						continue;
					// implicit implementation only works for static methods if they are declared abstract in the interface
					if ((!method.IsStatic || method.IsAbstract)
					    && TypesHierarchyHelpers.MatchInterfaceMethod(method, analyzedMethod, implementedInterfaceRef)
					) {
						yield return new MethodNode(method) { Context = Context };
						yield break;
					}
				}
			}
		}

		bool CheckOverride(MethodOverride m, MethodDef interfaceMethod) {
			if (m.MethodDeclaration.ResolveMethodDef() is not { } method)
				return false;
			return CheckEquals(method, interfaceMethod);
		}

		// Methods defined by an interface are not excluded in general, because they can provide a default implementation for another interface.
		// Interface methods without an implementation are excluded through the abstract check.
		// Methods on interfaces can only implement methods of other interfaces via explicit interface implementation (checked via HasOverrides).
		public static bool CanShow(MethodDef method) => !method.IsAbstract
		                                                && (method.IsVirtual || method.IsStatic) && (!method.DeclaringType.IsInterface || method.HasOverrides);
	}
}

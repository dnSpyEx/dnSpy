/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.MSBuild {
	sealed class XamlTypeProjectFile : TypeProjectFile {
		public XamlTypeProjectFile(TypeDef type, string filename, DecompilationContext decompilationContext, IDecompiler decompiler, Func<TextWriter, IDecompilerOutput> createDecompilerOutput)
			: base(type, filename, decompilationContext, decompiler, createDecompilerOutput) {
		}

		protected override void Decompile(DecompileContext ctx, IDecompilerOutput output) {
			var opts = new DecompilePartialType(output, decompilationContext, Type);
			foreach (var d in GetDefsToRemove(Type))
				opts.Definitions.Add(d);
			opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", "IComponentConnector", new AssemblyNameInfo("WindowsBase, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35").ToAssemblyRef()));
			opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", "IComponentConnector", new AssemblyNameInfo("System.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089").ToAssemblyRef()));
			opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", "IStyleConnector", new AssemblyNameInfo("PresentationFramework, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35").ToAssemblyRef()));
			opts.InterfacesToRemove.Add(new TypeRefUser(Type.Module, "System.Windows.Markup", "IStyleConnector", new AssemblyNameInfo("PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35").ToAssemblyRef()));
			decompiler.Decompile(DecompilationType.PartialType, opts);
		}

		/// <summary>
		/// The members the XAML compiler generates
		/// </summary>
		internal static IEnumerable<IMemberDef> GetDefsToRemove(TypeDef type) {
			var ep = type.Module.EntryPoint;
			if (ep is not null && ep.DeclaringType == type)
				yield return ep;

			var d = FindInitializeComponent(type);
			if (d is not null) {
				yield return d;
				foreach (var f in DotNetUtils.GetFields(d)) {
					if (f.FieldType.RemovePinnedAndModifiers().GetElementType() == ElementType.Boolean)
						yield return f;
				}
			}

			// IComponentConnector.Connect, and IStyleConnector.Connect if a style has an EventSetter
			foreach (var connMeth in FindConnectMethods(type)) {
				yield return connMeth;
				foreach (var f in DotNetUtils.GetFields(connMeth))
					yield return f;
			}

			var delMeth = FindCreateDelegateMethod(type);
			if (delMeth is not null)
				yield return delMeth;
		}

		static MethodDef? FindInitializeComponent(TypeDef type) {
			foreach (var md in type.FindMethods("InitializeComponent")) {
				if (md.IsStatic || md.Parameters.Count != 1)
					continue;
				if (md.ReturnType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void)
					continue;

				return md;
			}
			return null;
		}

		static readonly string[] connectorInterfaces = {
			"System.Windows.Markup.IComponentConnector",
			"System.Windows.Markup.IStyleConnector",
		};

		static List<MethodDef> FindConnectMethods(TypeDef type) {
			var methods = new List<MethodDef>();
			foreach (var md in type.Methods) {
				if (IsConnectSignature(md) && md.Overrides.Any(a => IsConnectorMethod(a.MethodDeclaration)))
					methods.Add(md);
			}
			// Not an explicit interface implementation: the first 'void Connect(int, object)'
			if (methods.Count == 0) {
				var md = type.Methods.FirstOrDefault(a => a.Name == "Connect" && IsConnectSignature(a));
				if (md is not null)
					methods.Add(md);
			}
			return methods;
		}

		static bool IsConnectorMethod(IMethodDefOrRef? method) =>
			method?.DeclaringType is ITypeDefOrRef declaringType && method.Name == "Connect" && Array.IndexOf(connectorInterfaces, declaringType.FullName) >= 0;

		static bool IsConnectSignature(MethodDef md) {
			if (md is null || md.IsStatic || md.Parameters.Count != 3)
				return false;
			if (md.ReturnType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void)
				return false;

			var sig = md.MethodSig;
			if (sig is null || sig.Params.Count != 2)
				return false;
			if (sig.Params[0].RemovePinnedAndModifiers().GetElementType() != ElementType.I4)
				return false;
			if (sig.Params[1].RemovePinnedAndModifiers().GetElementType() != ElementType.Object)
				return false;
			return true;
		}

		// Finds 'internal Delegate _CreateDelegate(Type delegateType, string handler)' which is
		// called by XamlGeneratedNamespace.GeneratedInternalTypeHelper.CreateDelegate()
		static MethodDef? FindCreateDelegateMethod(TypeDef type) {
			foreach (var m in type.Methods) {
				if (m.Name != "_CreateDelegate")
					continue;
				if (m.IsStatic || !m.IsAssembly)
					continue;
				var sig = m.MethodSig;
				if (sig.GetParamCount() != 2)
					continue;
				if (sig.RetType.RemovePinnedAndModifiers() is null || sig.RetType.RemovePinnedAndModifiers().ToString() != "System.Delegate")
					continue;
				if (sig.Params[0].RemovePinnedAndModifiers() is null || sig.Params[0].RemovePinnedAndModifiers().ToString() != "System.Type")
					continue;
				if (sig.Params[1].RemovePinnedAndModifiers() is null || sig.Params[1].RemovePinnedAndModifiers().GetElementType() != ElementType.String)
					continue;

				return m;
			}
			return null;
		}
	}
}

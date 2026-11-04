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
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.BamlDecompiler.Xaml;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.BamlDecompiler.Rewrite {
	/// <summary>
	/// App.xaml: the XAML compiler turns the Application element's StartupUri and event handlers
	/// (Startup="App_Startup") into code in the class's InitializeComponent, the BAML only has the rest (the resources).
	/// They're added back as attributes. Without them, an exported app doesn't open its window or run its startup code.
	/// </summary>
	sealed class ApplicationRewritePass : IRewritePass {
		public void Run(XamlContext ctx, XDocument document) {
			var root = document.Root?.Elements().FirstOrDefault();
			var xClass = root?.Attribute(ctx.GetKnownNamespace("Class", XamlContext.KnownNamespace_Xaml));
			if (root is null || xClass is null)
				return;
			var type = ctx.Module.Find(xClass.Value, true);
			if (type is null || !IsApplication(type))
				return;
			var initializeComponent = type.FindMethods("InitializeComponent").FirstOrDefault(a => !a.IsStatic && a.MethodSig.GetParamCount() == 0 && a.Body is not null);
			if (initializeComponent is null)
				return;
			foreach (var (name, value) in GetAttributes(initializeComponent)) {
				var xName = XName.Get(XmlConvert.EncodeLocalName(name));
				if (root.Attribute(xName) is null)
					root.Add(new XAttribute(xName, value));
			}
		}

		static bool IsApplication(TypeDef type) {
			int depth = 0;
			for (var baseType = type.BaseType; baseType is not null && depth < 100; baseType = baseType.ResolveTypeDef()?.BaseType, depth++) {
				if (baseType.FullName == "System.Windows.Application")
					return true;
			}
			return false;
		}

		/// <summary>
		/// The attributes in the code the XAML compiler generates:
		/// 'this.Startup += new StartupEventHandler(this.App_Startup);' and
		/// 'this.StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);'
		/// </summary>
		internal static List<(string name, string value)> GetAttributes(MethodDef initializeComponent) {
			var result = new List<(string name, string value)>();
			var instrs = initializeComponent.Body.Instructions.Where(a => a.OpCode.Code != Code.Nop).ToList();
			for (int i = 2; i < instrs.Count; i++) {
				var call = instrs[i];
				if ((call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt) || call.Operand is not IMethod method || method.MethodSig?.GetParamCount() != 1)
					continue;
				if (instrs[i - 1].OpCode.Code != Code.Newobj || instrs[i - 1].Operand is not IMethod ctor)
					continue;
				var name = method.Name.String;
				if (name.StartsWith("add_", StringComparison.Ordinal)) {
					var ldftn = instrs[i - 2];
					if ((ldftn.OpCode.Code == Code.Ldftn || ldftn.OpCode.Code == Code.Ldvirtftn) && ldftn.Operand is IMethod handler)
						result.Add((name.Substring(4), IdentifierEscaper.Escape(handler.Name)));
				}
				else if (name == "set_StartupUri" && ctor.DeclaringType?.FullName == "System.Uri") {
					// new Uri("MainWindow.xaml") or new Uri("MainWindow.xaml", UriKind.Relative)
					var ldstr = instrs[i - 2].OpCode.Code == Code.Ldstr || i < 3 ? instrs[i - 2] : instrs[i - 3];
					if (ldstr.OpCode.Code == Code.Ldstr && ldstr.Operand is string uri)
						result.Add(("StartupUri", uri));
				}
			}
			return result;
		}
	}
}

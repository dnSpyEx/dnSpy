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

using System.Linq;
using System.Xml.Linq;

namespace dnSpy.BamlDecompiler.Rewrite {
	/// <summary>
	/// An element that declares a prefix that an ancestor declares for another namespace, while it or its descendants
	/// still use that other namespace. E.g. .NET's Fluent theme: the root declares 'sys' for System in mscorlib, a merged
	/// dictionary's elements declare 'sys' for System in System.Runtime, and their names are mscorlib's 'Double' (WPF's
	/// known types are in mscorlib). The XML writer uses the ancestor's prefix and can't write the element ("The prefix
	/// 'sys' cannot be redefined from ... to ... within the same start element tag"). The element gets a prefix of its
	/// own for the other namespace ('sys1').
	/// </summary>
	sealed class XmlnsConflictRewritePass : IRewritePass {
		public void Run(XamlContext ctx, XDocument document) {
			foreach (var elem in document.Descendants().ToList()) {
				foreach (var decl in elem.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns).ToList()) {
					var prefix = decl.Name.LocalName;
					var shadowed = elem.Parent?.GetNamespaceOfPrefix(prefix);
					if (shadowed is null || shadowed.NamespaceName == decl.Value)
						continue;
					if (elem.Attributes().Any(a => a.IsNamespaceDeclaration && a.Value == shadowed.NamespaceName))
						continue;
					if (!elem.DescendantsAndSelf().Any(e => e.Name.Namespace == shadowed || e.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace == shadowed)))
						continue;
					int count = 1;
					while (elem.GetNamespaceOfPrefix(prefix + count) is not null)
						count++;
					elem.Add(new XAttribute(XNamespace.Xmlns + (prefix + count), shadowed.NamespaceName));
				}
			}
		}
	}
}

// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
//
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
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.Decompiler.Legacy.Ast;
using ICSharpCode.Decompiler.Legacy.VB.Visitors;

namespace dnSpy.Decompiler.ILSpy.Core.VisualBasic {
	sealed class ILSpyEnvironmentProvider : IEnvironmentProvider {
		public string RootNamespace => "";

		readonly StringBuilder sb;

		public ILSpyEnvironmentProvider(StringBuilder? sb = null) => this.sb = sb ?? new StringBuilder();

		public string GetTypeNameForAttribute(ICSharpCode.Decompiler.CSharp.Syntax.Attribute attribute) {
			var mr = attribute.Type.Annotations
				.OfType<IMemberRef>()
				.FirstOrDefault();
			return mr is null ? string.Empty : mr.FullName;
		}

		/*
var annotation = type.Annotation<TypeReference>();
if (annotation is null )
	return null;

IEntity current = null;
if (entity is not null) {
	var typeInfo = entity.Annotation<TypeReference>();
	current = loader.ReadTypeReference(typeInfo).Resolve(context).GetDefinition();
}

return loader.ReadTypeReference(annotation, entity: current).Resolve(context); */
		public ICSharpCode.Decompiler.TypeSystem.IType ResolveType(ICSharpCode.Decompiler.Legacy.VB.Ast.TypeName.AstType type, ICSharpCode.Decompiler.Legacy.VB.Ast.GlobalScope.TypeDeclaration? entity = null) => ICSharpCode.Decompiler.TypeSystem.SpecialType.UnknownType;

		public ICSharpCode.Decompiler.TypeSystem.TypeKind GetTypeKindForAstType(ICSharpCode.Decompiler.CSharp.Syntax.AstType type) {
			var annotation = type.Annotation<ITypeDefOrRef>();
			if (annotation is null)
				return ICSharpCode.Decompiler.TypeSystem.TypeKind.Unknown;

			var definition = annotation.ResolveTypeDef();
			if (definition is null)
				return ICSharpCode.Decompiler.TypeSystem.TypeKind.Unknown;
			if (definition.IsClass)
				return ICSharpCode.Decompiler.TypeSystem.TypeKind.Class;
			if (definition.IsInterface)
				return ICSharpCode.Decompiler.TypeSystem.TypeKind.Interface;
			if (definition.IsEnum)
				return ICSharpCode.Decompiler.TypeSystem.TypeKind.Enum;
			if (definition.IsValueType)
				return ICSharpCode.Decompiler.TypeSystem.TypeKind.Struct;

			return ICSharpCode.Decompiler.TypeSystem.TypeKind.Unknown;
		}

		public TypeCode ResolveExpression(ICSharpCode.Decompiler.CSharp.Syntax.Expression expression) {
			var annotation = expression.Annotations.OfType<TypeInformation>().FirstOrDefault();

			if (annotation is null || annotation.InferredType is null)
				return TypeCode.Object;

			var definition = annotation.InferredType.GetScopeTypeDefOrRef().ResolveTypeDef();

			if (definition is null)
				return TypeCode.Object;

			switch (definition.FullName) {
			case "System.String":
				return TypeCode.String;
			default:
				break;
			}

			return TypeCode.Object;
		}

		public bool? IsReferenceType(ICSharpCode.Decompiler.CSharp.Syntax.Expression expression) {
			if (expression is ICSharpCode.Decompiler.CSharp.Syntax.NullReferenceExpression)
				return true;

			var annotation = expression.Annotations.OfType<TypeInformation>().FirstOrDefault();

			if (annotation is null || annotation.InferredType is null)
				return null;

			var definition = annotation.InferredType.GetScopeTypeDefOrRef().ResolveTypeDef();

			if (definition is null)
				return null;

			return !definition.IsValueType;
		}

		public IEnumerable<ICSharpCode.Decompiler.Legacy.VB.Ast.General.InterfaceMemberSpecifier> CreateMemberSpecifiersForInterfaces(IEnumerable<ICSharpCode.Decompiler.Legacy.VB.Ast.TypeName.AstType> interfaces) {
			foreach (var type in interfaces) {
				var def = type.Annotation<ITypeDefOrRef>().ResolveTypeDef();
				if (def is null)
					continue;
				foreach (var method in def.Methods.Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))) {
					yield return ICSharpCode.Decompiler.Legacy.VB.Ast.General.InterfaceMemberSpecifier.CreateWithColor((ICSharpCode.Decompiler.Legacy.VB.Ast.TypeName.AstType)type.Clone(), method.Name, VisualBasicMetadataTextColorProvider.Instance.GetColor(method));
				}

				foreach (var property in def.Properties) {
					yield return ICSharpCode.Decompiler.Legacy.VB.Ast.General.InterfaceMemberSpecifier.CreateWithColor((ICSharpCode.Decompiler.Legacy.VB.Ast.TypeName.AstType)type.Clone(), property.Name, VisualBasicMetadataTextColorProvider.Instance.GetColor(property));
				}
			}
		}

		public bool HasEvent(ICSharpCode.Decompiler.Legacy.VB.Ast.Expressions.Expression expression) => expression.Annotation<EventDef>() is not null;

		public bool IsMethodGroup(ICSharpCode.Decompiler.CSharp.Syntax.Expression expression) {
			var annotation = expression.Annotation<MethodDef>();
			if (annotation is null)
				return false;
			return expression.Annotation<PropertyDef>() is null && expression.Annotation<EventDef>() is null;
		}

		public ICSharpCode.Decompiler.CSharp.Syntax.ParameterDeclaration[] GetParametersForProperty(ICSharpCode.Decompiler.CSharp.Syntax.PropertyDeclaration property) {
			var propInfo = property.Annotation<PropertyDef>();

			if (propInfo is null)
				return Array.Empty<ICSharpCode.Decompiler.CSharp.Syntax.ParameterDeclaration>();

			sb.Clear();
			var getMethod = propInfo.GetMethod;
			if (getMethod is not null)
				return getMethod.Parameters.Where(p => p.IsNormalMethodParameter).Select(p => new ICSharpCode.Decompiler.CSharp.Syntax.ParameterDeclaration(AstBuilder.ConvertType(p.Type, sb), p.Name, GetModifiers(p))).ToArray();
			var setMethod = propInfo.SetMethod;
			if (setMethod is not null) {
				var ps = setMethod.Parameters.Where(p => p.IsNormalMethodParameter).ToArray();
				if (ps.Length > 1)
					return ps.Take(ps.Length - 1).Select(p => new ICSharpCode.Decompiler.CSharp.Syntax.ParameterDeclaration(AstBuilder.ConvertType(p.Type, sb), p.Name, GetModifiers(p))).ToArray();
			}

			return Array.Empty<ICSharpCode.Decompiler.CSharp.Syntax.ParameterDeclaration>();
		}

		ICSharpCode.Decompiler.TypeSystem.ReferenceKind GetModifiers(Parameter p) {
			var pd = p.ParamDef;
			if (pd is not null) {
				if (pd.IsOut && pd.IsIn)
					return ICSharpCode.Decompiler.TypeSystem.ReferenceKind.Ref;
				if (pd.IsOut)
					return ICSharpCode.Decompiler.TypeSystem.ReferenceKind.Out;
			}

			return ICSharpCode.Decompiler.TypeSystem.ReferenceKind.None;
		}
	}
}

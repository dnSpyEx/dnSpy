/*
    The English strings below are from ILSpy's resources (ILSpy/Properties/Resources.resx), used under the MIT license:

    Copyright (c) 2011-2026 AlphaSierraPapa for the SharpDevelop Team

    Permission is hereby granted, free of charge, to any person obtaining a copy of this
    software and associated documentation files (the "Software"), to deal in the Software
    without restriction, including without limitation the rights to use, copy, modify, merge,
    publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
    to whom the Software is furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all copies or
    substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
    INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
    PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
    FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
    OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
    DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Text;

namespace dnSpy.Decompiler.ILSpy.Core.Settings {
	/// <summary>
	/// Display strings of the upstream decompiler's settings. The settings' <see cref="System.ComponentModel.DescriptionAttribute"/>
	/// and <see cref="System.ComponentModel.CategoryAttribute"/> values are keys of ILSpy's (UI) resources, which aren't
	/// part of the decompiler package. Settings added by later versions get a name derived from the property name.
	/// </summary>
	static class SettingDescriptions {
		static readonly Dictionary<string, string> strings = new Dictionary<string, string>(StringComparer.Ordinal) {
			{ "DecompilerSettings.AggressiveInlining", "Always inline local variables if possible" },
			{ "DecompilerSettings.AllowExtensionAddMethodsInCollectionInitializerExpressions", "Allow extension 'Add' methods in collection initializer expressions" },
			{ "DecompilerSettings.AllowExtensionMethodSyntaxOnRef", "Use 'ref' extension methods" },
			{ "DecompilerSettings.AlwaysCastTargetsOfExplicitInterfaceImplementationCalls", "Always cast targets of explicit interface implementation calls" },
			{ "DecompilerSettings.AlwaysMoveInitializer", "Always move field initializers from constructors to declarations" },
			{ "DecompilerSettings.AlwaysQualifyMemberReferences", "Always qualify member references" },
			{ "DecompilerSettings.AlwaysShowEnumMemberValues", "Always show enum member values" },
			{ "DecompilerSettings.AlwaysUseBraces", "Always use braces" },
			{ "DecompilerSettings.AlwaysUseGlobal", "Always fully qualify namespaces using the \"global::\" prefix" },
			{ "DecompilerSettings.ApplyWindowsRuntimeProjectionsOnLoadedAssemblies", "Apply Windows Runtime projections on loaded assemblies" },
			{ "DecompilerSettings.ArrayInitializerExpressions", "Array initializer expressions" },
			{ "DecompilerSettings.AsyncEnumerator", "Decompile async IAsyncEnumerator methods" },
			{ "DecompilerSettings.AutoLoadAssemblyReferences", "Automatically load assembly references" },
			{ "DecompilerSettings.CheckForOverflowUnderflow", "Check for overflow and underflow in operators" },
			{ "DecompilerSettings.CheckedOperators", "User-defined checked operators" },
			{ "DecompilerSettings.CovariantReturns", "Covariant return types" },
			{ "DecompilerSettings.DecompileAnonymousMethodsLambdas", "Decompile anonymous methods/lambdas" },
			{ "DecompilerSettings.DecompileAnonymousTypes", "Decompile anonymous types" },
			{ "DecompilerSettings.DecompileAsyncMethods", "Decompile async methods" },
			{ "DecompilerSettings.DecompileAutomaticEvents", "Decompile automatic events" },
			{ "DecompilerSettings.DecompileAutomaticProperties", "Decompile automatic properties" },
			{ "DecompilerSettings.DecompileAwaitInCatchFinallyBlocks", "Decompile await in catch/finally blocks" },
			{ "DecompilerSettings.DecompileC10PublicUnsafeFixedIntArr10Members", "Decompile C# 1.0 'public unsafe fixed int arr[10];' members" },
			{ "DecompilerSettings.DecompileDecimalConstantAsSimpleLiteralValues", "Decompile [DecimalConstant(...)] as simple literal values" },
			{ "DecompilerSettings.DecompileEnumeratorsYieldReturn", "Decompile enumerators (yield return)" },
			{ "DecompilerSettings.DecompileExpressionTrees", "Decompile expression trees" },
			{ "DecompilerSettings.DecompileForEachWithGetEnumeratorExtension", "Decompile foreach statements with GetEnumerator extension methods" },
			{ "DecompilerSettings.DecompileParamsCollections", "Decompile params collections" },
			{ "DecompilerSettings.DecompileUseOfTheDynamicType", "Decompile use of the 'dynamic' type" },
			{ "DecompilerSettings.Deconstruction", "Detect deconstruction assignments" },
			{ "DecompilerSettings.DetectAsyncUsingAndForeachStatements", "Detect awaited using and foreach statements" },
			{ "DecompilerSettings.DetectForeachStatements", "Detect foreach statements" },
			{ "DecompilerSettings.DetectLockStatements", "Detect lock statements" },
			{ "DecompilerSettings.DetectSwitchOnString", "Detect switch on string" },
			{ "DecompilerSettings.DetectTupleComparisons", "Detect tuple comparisons" },
			{ "DecompilerSettings.DetectUsingStatements", "Detect using statements" },
			{ "DecompilerSettings.DictionaryInitializerExpressions", "Dictionary initializer expressions" },
			{ "DecompilerSettings.DoWhileStatement", "Transform to do-while, if possible" },
			{ "DecompilerSettings.ExpandParamsArguments", "Expand params arguments by removing explicit array creation" },
			{ "DecompilerSettings.ExtensionMembers", "Decompile extension members" },
			{ "DecompilerSettings.FSpecificOptions", "F#-specific options" },
			{ "DecompilerSettings.FieldKeyword", "Use the 'field' keyword in property accessors" },
			{ "DecompilerSettings.FileScopedNamespaces", "Use file-scoped namespace declarations" },
			{ "DecompilerSettings.FirstClassSpanTypes", "Treat Span<T> and ReadOnlySpan<T> as built-in types" },
			{ "DecompilerSettings.ForStatement", "Transform to for, if possible" },
			{ "DecompilerSettings.FunctionPointers", "Function pointers" },
			{ "DecompilerSettings.GetterOnlyAutomaticProperties", "Decompile getter-only automatic properties" },
			{ "DecompilerSettings.IncludeXMLDocumentationCommentsInTheDecompiledCode", "Include XML documentation comments in the decompiled code" },
			{ "DecompilerSettings.InitAccessors", "Allow init; accessors" },
			{ "DecompilerSettings.InlineArrays", "Inline arrays" },
			{ "DecompilerSettings.InsertUsingDeclarations", "Insert using declarations" },
			{ "DecompilerSettings.IntroduceLocalFunctions", "Introduce local functions" },
			{ "DecompilerSettings.IntroducePrivateProtectedAccessibility", "Introduce 'private protected' accessibility" },
			{ "DecompilerSettings.IntroduceStaticLocalFunctions", "Introduce static local functions" },
			{ "DecompilerSettings.IsByRefLikeAttributeShouldBeReplacedWithRefModifiersOnStructs", "IsByRefLikeAttribute should be replaced with 'ref' modifiers on structs" },
			{ "DecompilerSettings.IsReadOnlyAttributeShouldBeReplacedWithReadonlyInModifiersOnStructsParameters", "IsReadOnlyAttribute should be replaced with 'readonly'/'in' modifiers on structs/parameters" },
			{ "DecompilerSettings.IsUnmanagedAttributeOnTypeParametersShouldBeReplacedWithUnmanagedConstraints", "IsUnmanagedAttribute on type parameters should be replaced with 'unmanaged' constraints" },
			{ "DecompilerSettings.LambdaOptionalAndParamsParameters", "'params' and optional parameters in lambdas" },
			{ "DecompilerSettings.NativeIntegers", "Use nint/nuint types" },
			{ "DecompilerSettings.NullPropagation", "Decompile ?. and ?[] operators" },
			{ "DecompilerSettings.NullableReferenceTypes", "Nullable reference types" },
			{ "DecompilerSettings.NumericIntPtr", "Treat (U)IntPtr as n(u)int" },
			{ "DecompilerSettings.ObjectCollectionInitializerExpressions", "Object/collection initializer expressions" },
			{ "DecompilerSettings.Other", "Other" },
			{ "DecompilerSettings.PatternCombinators", "Pattern combinators (and, or, not)" },
			{ "DecompilerSettings.PatternMatching", "Use pattern matching expressions" },
			{ "DecompilerSettings.ProjectExport", "Project export" },
			{ "DecompilerSettings.Ranges", "Ranges" },
			{ "DecompilerSettings.ReadOnlyMethods", "Read-only methods" },
			{ "DecompilerSettings.RecordClasses", "Record classes" },
			{ "DecompilerSettings.RecordStructs", "Record structs" },
			{ "DecompilerSettings.RecursivePatternMatching", "Recursive pattern matching" },
			{ "DecompilerSettings.RefReadOnlyParameters", "'ref readonly' parameters" },
			{ "DecompilerSettings.RelationalPatterns", "Relational patterns" },
			{ "DecompilerSettings.RemoveDeadAndSideEffectFreeCodeUseWithCaution", "Remove dead and side effect free code (use with caution!)" },
			{ "DecompilerSettings.RemoveDeadStores", "Remove dead stores (use with caution!)" },
			{ "DecompilerSettings.RemoveOptionalArgumentsIfPossible", "Remove optional arguments, if possible" },
			{ "DecompilerSettings.RequiredMembers", "Required members" },
			{ "DecompilerSettings.ScopedRef", "'scoped' lifetime annotation" },
			{ "DecompilerSettings.SeparateLocalVariableDeclarations", "Separate local variable declarations and initializers (int x = 5; -> int x; x = 5;), if possible" },
			{ "DecompilerSettings.ShowInfoFromDebugSymbolsIfAvailable", "Show info from debug symbols, if available" },
			{ "DecompilerSettings.SortCustomAttributes", "Sort custom attributes" },
			{ "DecompilerSettings.SortSwitchSections", "Sort switch sections by case label value" },
			{ "DecompilerSettings.SparseIntegerSwitch", "Detect switch on integer even if IL code does not use a jump table" },
			{ "DecompilerSettings.StringConcat", "Decompile 'string.Concat(a, b)' calls into 'a + b'" },
			{ "DecompilerSettings.StructDefaultConstructorsAndFieldInitializers", "Use struct field initializers and default constructors" },
			{ "DecompilerSettings.SwitchExpressions", "Switch expressions" },
			{ "DecompilerSettings.SwitchOnReadOnlySpanChar", "Use switch on (ReadOnly)Span<char>" },
			{ "DecompilerSettings.UnsignedRightShift", "Unsigned right shift (>>>)" },
			{ "DecompilerSettings.UseDefaultLiterals", "Use \"default\" literals without an explicit type" },
			{ "DecompilerSettings.UseDiscards", "Use discards" },
			{ "DecompilerSettings.UseEnhancedUsing", "Use enhanced using variable declarations" },
			{ "DecompilerSettings.UseExpressionBodiedMemberSyntaxForGetOnlyProperties", "Use expression-bodied member syntax for get-only properties" },
			{ "DecompilerSettings.UseExtensionMethodSyntax", "Use extension method syntax" },
			{ "DecompilerSettings.UseImplicitConversionsBetweenTupleTypes", "Use implicit conversions between tuple types" },
			{ "DecompilerSettings.UseImplicitMethodGroupConversions", "Use implicit method group conversions" },
			{ "DecompilerSettings.UseLINQExpressionSyntax", "Use LINQ expression syntax" },
			{ "DecompilerSettings.UseLambdaSyntaxIfPossible", "Use lambda syntax, if possible" },
			{ "DecompilerSettings.UseLiftedOperatorsForNullables", "Use lifted operators for nullables" },
			{ "DecompilerSettings.UseNamedArguments", "Use named arguments" },
			{ "DecompilerSettings.UseNestedDirectoriesForNamespaces", "Use nested directories for namespaces" },
			{ "DecompilerSettings.UseNonTrailingNamedArguments", "Use non-trailing named arguments" },
			{ "DecompilerSettings.UseObjectCreationOfGenericTypeParameter", "Use new T() instead of Activator.CreateInstance<T>" },
			{ "DecompilerSettings.UseOutVariableDeclarations", "Use out variable declarations" },
			{ "DecompilerSettings.UsePatternBasedFixedStatement", "Use pattern-based fixed statement" },
			{ "DecompilerSettings.UsePrimaryConstructorSyntax", "Use primary constructor syntax with records" },
			{ "DecompilerSettings.UsePrimaryConstructorSyntaxForNonRecordTypes", "Use primary constructor syntax for non-record types" },
			{ "DecompilerSettings.UseRefLocalsForAccurateOrderOfEvaluation", "Use ref locals to accurately represent order of evaluation" },
			{ "DecompilerSettings.UseSdkStyleProjectFormat", "Use new SDK style format for generated project files (*.csproj)" },
			{ "DecompilerSettings.UseStackallocInitializerSyntax", "Use stackalloc initializer syntax" },
			{ "DecompilerSettings.UseStringInterpolation", "Use string interpolation" },
			{ "DecompilerSettings.UseThrowExpressions", "Use throw expressions" },
			{ "DecompilerSettings.UseTupleTypeSyntax", "Use tuple type syntax" },
			{ "DecompilerSettings.UseVariableNamesFromDebugSymbolsIfAvailable", "Use variable names from debug symbols, if available" },
			{ "DecompilerSettings.Utf8StringLiterals", "UTF-8 string literals" },
			{ "DecompilerSettings.WithExpressions", "'with' initializer expressions" },
		};

		/// <summary>
		/// Gets the display string of a resource key, or null if it's unknown
		/// </summary>
		public static string? TryGet(string? key) {
			if (key is null)
				return null;
			return strings.TryGetValue(key, out var value) ? value : null;
		}

		/// <summary>
		/// "UseNestedDirectories" -> "Use nested directories"
		/// </summary>
		public static string Humanize(string name) {
			var sb = new StringBuilder(name.Length + 8);
			for (int i = 0; i < name.Length; i++) {
				char c = name[i];
				if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
					sb.Append(' ').Append(i + 1 < name.Length && char.IsLower(name[i + 1]) ? char.ToLowerInvariant(c) : c);
				else
					sb.Append(c);
			}
			return sb.ToString();
		}
	}
}

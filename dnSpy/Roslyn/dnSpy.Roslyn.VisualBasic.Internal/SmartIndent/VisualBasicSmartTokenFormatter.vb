' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Threading
Imports dnSpy.Roslyn.Internal.SmartIndent
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.Collections
Imports Microsoft.CodeAnalysis.Formatting
Imports Microsoft.CodeAnalysis.Formatting.Rules
Imports Microsoft.CodeAnalysis.Options
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports Microsoft.CodeAnalysis.VisualBasic.Formatting
Imports Roslyn.Utilities

Namespace Global.dnSpy.Roslyn.VisualBasic.Internal.SmartIndent
	Friend Class VisualBasicSmartTokenFormatter
		Implements ISmartTokenFormatter

		Private ReadOnly _options As SyntaxFormattingOptions
		Private ReadOnly _formattingRules As ImmutableArray(Of AbstractFormattingRule)

		Private ReadOnly _root As CompilationUnitSyntax

		Public Sub New(options As SyntaxFormattingOptions,
		               formattingRules As ImmutableArray(Of AbstractFormattingRule),
		               root As CompilationUnitSyntax)
			Contract.ThrowIfNull(root)

			_options = options
			_formattingRules = formattingRules

			_root = root
		End Sub

		Public Function FormatToken(token As SyntaxToken, cancellationToken As CancellationToken) As IList(Of TextChange) Implements ISmartTokenFormatter.FormatToken
			Contract.ThrowIfTrue(token.IsKind(SyntaxKind.None) OrElse token.IsKind(SyntaxKind.EndOfFileToken))

			' get previous token
			Dim previousToken = token.GetPreviousToken()

			Dim spans = New TextSpan() {TextSpan.FromBounds(previousToken.SpanStart, token.Span.End)}
			Dim formatter = VisualBasicSyntaxFormatting.Instance
			Dim result = formatter.GetFormattingResult(_root, spans, _options, _formattingRules, cancellationToken)
			Return result.GetTextChanges(cancellationToken)
			End Function
	End Class
End Namespace

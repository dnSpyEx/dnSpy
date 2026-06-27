using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Formatting.Rules;

namespace dnSpy.Roslyn.Internal.SmartIndent {
	internal class VBHackCompatAbstractFormattingRule : CompatAbstractFormattingRule {
		public override void AddSuppressOperationsSlow(ArrayBuilder<SuppressOperation> list, SyntaxNode node,
			ref NextSuppressOperationAction nextOperation) {
			// don't suppress anything
		}
	}
}

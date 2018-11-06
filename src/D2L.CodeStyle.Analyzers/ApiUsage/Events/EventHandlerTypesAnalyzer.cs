using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace D2L.CodeStyle.Analyzers.ApiUsage.Events {

	[DiagnosticAnalyzer( LanguageNames.CSharp )]
	internal sealed class EventHandlerTypesAnalyzer : DiagnosticAnalyzer {

		private const string EventHandlerAttributeFullName = "D2L.LP.Distributed.Events.Handlers.EventHandlerAttribute";
		private const string ImmutableAttributeFullName = "D2L.CodeStyle.Annotations.Objects+Immutable";

		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
			Diagnostics.EventHandlerMissingImmutableAttribute,
			Diagnostics.InvalidEventHandlerId
		);

		public override void Initialize( AnalysisContext context ) {

			context.EnableConcurrentExecution();
			context.RegisterCompilationStartAction( RegisterAnalysis );
		}

		private void RegisterAnalysis( CompilationStartAnalysisContext context ) {

			Compilation compilation = context.Compilation;

			INamedTypeSymbol eventHandlerAttributeType = compilation.GetTypeByMetadataName( EventHandlerAttributeFullName );
			if( eventHandlerAttributeType == null ) {
				return;
			}

			INamedTypeSymbol immutableAttributeType = compilation.GetTypeByMetadataName( ImmutableAttributeFullName );

			context.RegisterSyntaxNodeAction(
					ctxt => AnalyzeMethodInvocation(
						ctxt,
						(ClassDeclarationSyntax)ctxt.Node,
						eventHandlerAttributeType,
						immutableAttributeType
					),
					SyntaxKind.ClassDeclaration
				);
		}

		private void AnalyzeMethodInvocation(
				SyntaxNodeAnalysisContext context,
				ClassDeclarationSyntax declaration,
				INamedTypeSymbol eventHandlerAttributeType,
				INamedTypeSymbol immutableAttributeType
			) {

			INamedTypeSymbol declarationType = context.SemanticModel.GetDeclaredSymbol( declaration );

			bool hasEventAttribute = HasAttribute( declarationType, eventHandlerAttributeType );
			if( !hasEventAttribute ) {
				return;
			}

			bool hasImmutableAttirbute = HasAttribute( declarationType, immutableAttributeType );
			if( hasImmutableAttirbute ) {
				return;
			}

			Diagnostic diagnostic = Diagnostic.Create(
					Diagnostics.EventHandlerMissingImmutableAttribute,
					declaration.Identifier.GetLocation(),
					declarationType.ToDisplayString()
				);

			context.ReportDiagnostic( diagnostic );
		}

		private static bool HasAttribute(
				INamedTypeSymbol declarationType,
				INamedTypeSymbol attributeType
			) {

			if( attributeType == null ) {
				return false;
			}

			bool hasAttribute = declarationType
				.GetAttributes()
				.Any( attr => attr.AttributeClass.Equals( attributeType ) );

			return hasAttribute;
		}
	}
}

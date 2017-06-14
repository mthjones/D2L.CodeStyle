﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using D2L.CodeStyle.Analyzers.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace D2L.CodeStyle.Analyzers.UnsafeStatics {
	[DiagnosticAnalyzer( LanguageNames.CSharp )]
	public sealed class UnsafeStaticsAnalyzer : DiagnosticAnalyzer {

		public const string PROPERTY_FIELDORPROPNAME = "FieldOrProprName";
		public const string PROPERTY_OFFENDINGTYPE = "OffendingType";

		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
			Diagnostics.UnsafeStatic,
			Diagnostics.ConflictingStaticAnnotation,
			Diagnostics.UnnecessaryStaticAnnotation
		);

		private readonly MutabilityInspector m_immutabilityInspector = new MutabilityInspector();
		private readonly Utils m_utils = new Utils();
		private readonly MutabilityInspectionResultFormatter m_resultFormatter = new MutabilityInspectionResultFormatter();

		public override void Initialize( AnalysisContext context ) {
			context.RegisterSyntaxNodeAction(
				AnalyzeField,
				SyntaxKind.FieldDeclaration
			);

			context.RegisterSyntaxNodeAction(
				AnalyzeProperty,
				SyntaxKind.PropertyDeclaration
			);
		}

		private void AnalyzeField( SyntaxNodeAnalysisContext context ) {
			if( m_utils.IsGeneratedCodefile( context.Node.SyntaxTree.FilePath ) ) {
				// skip code-gen'd files; they have been hand-inspected to be safe
				return;
			}

			var root = context.Node as FieldDeclarationSyntax;

			if( root == null ) {
				throw new Exception( "This should not happen if this function is wired up correctly" );
			}

			bool isStatic = root.Modifiers.Any( SyntaxKind.StaticKeyword );
			bool isReadOnly = root.Modifiers.Any( SyntaxKind.ReadOnlyKeyword );

			foreach( var variable in root.Declaration.Variables ) {
				var symbol = context.SemanticModel.GetDeclaredSymbol( variable ) as IFieldSymbol;

				if( symbol == null ) {
					// Could this happen? We are not emitting diagnostics in this
					// case even when the fields have an annotation.
					continue;
				}

				InspectFieldOrProperty(
					context,
					location: variable.GetLocation(),
					attributeLists: root.AttributeLists,
					isStatic: isStatic,
					isReadOnly: isReadOnly,
					fieldOrPropertyType: symbol.Type,
					fieldOrPropertyName: variable.Identifier.ValueText,
					initializer: variable.Initializer?.Value,
					isPropertyGetterImplemented: false
				);
			}
		}

		private void AnalyzeProperty( SyntaxNodeAnalysisContext context ) {
			if( m_utils.IsGeneratedCodefile( context.Node.SyntaxTree.FilePath ) ) {
				// skip code-gen'd files; they have been hand-inspected to be safe
				return;
			}

			var root = context.Node as PropertyDeclarationSyntax;

			if( root == null ) {
				throw new Exception( "This should not happen if this function is wired up correctly" );
			}

			bool isStatic = root.Modifiers.Any( SyntaxKind.StaticKeyword );

			var prop = context.SemanticModel.GetDeclaredSymbol( root );

			if( prop == null ) {
				// Could this happen? We are not emitting diagnostics in this case
				// even when the property has an annotation.
				return;
			}

			bool isPropertyGetterImplemented = root.IsPropertyGetterImplemented();

			InspectFieldOrProperty(
				context,
				location: root.GetLocation(),
				attributeLists: root.AttributeLists,
				isStatic: isStatic,
				isReadOnly: prop.IsReadOnly,
				fieldOrPropertyType: prop.Type,
				fieldOrPropertyName: prop.Name,
				initializer: root.Initializer?.Value,
				isPropertyGetterImplemented: isPropertyGetterImplemented
			);
		}

		/// <summary>
		/// This helper method implements all of the shared logic. We have to
		/// split this for two reasons:
		///
		/// 1. PropertyDeclarationSyntax and FieldDeclarationSyntaxs useful
		///    members don't come from a base class/interface so we can't do
		///    this generically.
		/// 2. Fields can define multiple variables and we may wish to output
		///    multiple diagnostics (individual ariables in a field declaration
		///    may be alright because of their initializers.)
		///
		/// The arguments are organized roughly based on the common grammar of
		/// fields and properties: attributes, modifiers, type, name,
		/// initializer.
		/// </summary>
		private void InspectFieldOrProperty(
			SyntaxNodeAnalysisContext context,
			Location location,
			SyntaxList<AttributeListSyntax> attributeLists,
			bool isStatic,
			bool isReadOnly,
			ITypeSymbol fieldOrPropertyType,
			string fieldOrPropertyName,
			ExpressionSyntax initializer,
			bool isPropertyGetterImplemented // only applies to properties
		) {
			var diagnostics = GatherDiagnostics(
				context.SemanticModel,
				location: location,
				isStatic: isStatic,
				isReadonly: isReadOnly,
				fieldOrPropertyType: fieldOrPropertyType,
				fieldOrPropertyName: fieldOrPropertyName,
				initializationExpression: initializer,
				isPropertyGetterImplemented: isPropertyGetterImplemented
			);

			var attributes = attributeLists
				.SelectMany( al => al.Attributes )
				.ToImmutableArray();

			// We're manually using enumerators here.
			// - if we used IEnumerable directly we'd re-compute the first
			//   diagnostic in the GatherDiagnostics generator
			// - if we did .ToArray() early then we would avoid multiple
			//   enumeration but would compute diagnostics even when we
			//   ultimately ignore them due to annotations
			using( var enumerator = diagnostics.GetEnumerator() ) {
				ProcessDiagnostics( context, enumerator, location, attributes );
			}
		}
		
		private void ProcessDiagnostics(
			SyntaxNodeAnalysisContext context,
			IEnumerator<Diagnostic> enumerator,
			Location location,
			ImmutableArray<AttributeSyntax> attributes
		) {
			var hasDiagnostics = enumerator.MoveNext();

			// TODO: This EndsWith stuff is lame. We should do the same thing
			// that the RpcAnalyzer does with looking up and caching the types.
			bool hasUnauditedAnnotation = attributes
				.Any( a => a.Name.ToFullString().EndsWith( ".Unaudited" ) );

			// TODO: This EndsWith stuff is lame. We should do the same thing
			// that the RpcAnalyzer does with looking up and caching the types.
			bool hasAuditedAnnotation = attributes
				.Any( a => a.Name.ToFullString().EndsWith( ".Audited" ) );

			if ( hasAuditedAnnotation && hasUnauditedAnnotation ) {
				context.ReportDiagnostic(
					Diagnostic.Create(
						Diagnostics.ConflictingStaticAnnotation,
						location
					)
				);

				// Bail out here because it's unclear which of the remaining
				// diagnostics for this field/property should apply.
				return;
			}

			bool hasAnnotations = hasAuditedAnnotation || hasUnauditedAnnotation;

			if ( hasAnnotations && !hasDiagnostics ) {
				context.ReportDiagnostic(
					Diagnostic.Create(
						Diagnostics.UnnecessaryStaticAnnotation,
						location
					)
				);

				// this bail-out isn't important because !hasDiagnostics
				return;
			}

			if ( hasAnnotations ) {
				// the annotations supress remaining diagnostics
				return;
			}

			while ( hasDiagnostics ) {
				context.ReportDiagnostic( enumerator.Current );
				hasDiagnostics = enumerator.MoveNext();
			}
		}

		/// <summary>
		/// All logic relating to either emitting or not emitting diagnostics other
		/// than the ones about unnecessary annotations belong in this function.
		/// This allows InspectMember to implement the logic around the unnecessary
		/// annotations diagnostic. Any time we bail early in AnalyzeField or
		/// AnalyzeProperty we risk not emitting unnecessary annotation
		/// diagnostics.
		/// </summary>
		private IEnumerable<Diagnostic> GatherDiagnostics(
			SemanticModel model,
			Location location,
			bool isStatic,
			bool isReadonly,
			ITypeSymbol fieldOrPropertyType,
			string fieldOrPropertyName,
			ExpressionSyntax initializationExpression,
			bool isPropertyGetterImplemented
		) {
			if ( !isStatic ) {
				yield break;
			}

			if( isPropertyGetterImplemented ) {
				// things with getters are really just functions; they can't
				// hold state themselves.
				yield break;
			}

			if( !isReadonly ) {
				yield return CreateDiagnostic(
					location,
					fieldOrPropertyName,
					fieldOrPropertyType.GetFullTypeNameWithGenericArguments(),
					MutabilityInspectionResult.Mutable(
						fieldOrPropertyName,
						fieldOrPropertyType.GetFullTypeNameWithGenericArguments(),
						MutabilityTarget.Member,
						MutabilityCause.IsNotReadonly
					)
				);
			}

			if( m_immutabilityInspector.IsTypeMarkedImmutable( fieldOrPropertyType ) ) {
				// if the type is marked immutable, skip checking it, to avoid reporting
				// a diagnostic for each usage of non-immutable types that are marked
				// immutable (another analyzer catches this already.)
				yield break;
			}

			var flags = MutabilityInspectionFlags.Default;

			// Always prefer the type from the initializer if it exists because
			// it may be more specific.
			if( initializationExpression != null ) {
				var initializerType = model.GetTypeInfo( initializationExpression ).Type;

				// Fall back to the declaration type if we can't get a type for
				// the initializer.
				if( initializerType != null && !( initializerType is IErrorTypeSymbol ) ) {
					fieldOrPropertyType = initializerType;
				}
			}

			// When we know the concrete type as in "new T()" we don't have to
			// be paranoid about mutable derived classes.
			if ( initializationExpression is ObjectCreationExpressionSyntax ) {
				flags |= MutabilityInspectionFlags.AllowUnsealed;
			}

			var result = m_immutabilityInspector.InspectType( fieldOrPropertyType, flags );
			if ( result.IsMutable ) {
				result = result.WithPrefixedMember( fieldOrPropertyName );
				yield return CreateDiagnostic( 
					location, 
					fieldOrPropertyName, 
					fieldOrPropertyType.GetFullTypeNameWithGenericArguments(), 
					result 
				);
			}
		}

		private Diagnostic CreateDiagnostic( 
			Location location, 
			string fieldOrPropName, 
			string offendingType, 
			MutabilityInspectionResult result 
		) {
			var builder = ImmutableDictionary.CreateBuilder<string, string>();
			builder[PROPERTY_FIELDORPROPNAME] = fieldOrPropName;
			builder[PROPERTY_OFFENDINGTYPE] = offendingType;
			var properties = builder.ToImmutable();

			var reason = m_resultFormatter.Format( result );

			var diagnostic = Diagnostic.Create(
				Diagnostics.UnsafeStatic,
				location,
				properties,
				fieldOrPropName,
				reason
			);
			return diagnostic;
		}
	}
}

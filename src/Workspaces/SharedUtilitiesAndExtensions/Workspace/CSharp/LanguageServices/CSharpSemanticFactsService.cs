﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;
using Microsoft.CodeAnalysis.CSharp.LanguageService;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Utilities;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class CSharpSemanticFactsService : AbstractSemanticFactsService, ISemanticFactsService
    {
        internal static readonly CSharpSemanticFactsService Instance = new();

        public override ISyntaxFacts SyntaxFacts => CSharpSyntaxFacts.Instance;
        public override IBlockFacts BlockFacts => CSharpBlockFacts.Instance;

        protected override ISemanticFacts SemanticFacts => CSharpSemanticFacts.Instance;

        private CSharpSemanticFactsService()
        {
        }

        protected override SyntaxToken ToIdentifierToken(string identifier)
            => identifier.ToIdentifierToken();

        protected override IEnumerable<ISymbol> GetCollidableSymbols(SemanticModel semanticModel, SyntaxNode location, SyntaxNode container, CancellationToken cancellationToken)
        {
            // Get all the symbols visible to the current location.
            var visibleSymbols = semanticModel.LookupSymbols(location.SpanStart);

            // Local function parameter is allowed to shadow variables since C# 8.
            if (semanticModel.Compilation.LanguageVersion().MapSpecifiedToEffectiveVersion() >= LanguageVersion.CSharp8)
            {
                if (SyntaxFacts.IsParameterList(container) && SyntaxFacts.IsLocalFunctionStatement(container.Parent))
                {
                    visibleSymbols = visibleSymbols.WhereAsArray(s => !s.MatchesKind(SymbolKind.Local, SymbolKind.Parameter));
                }
            }

            // Some symbols in the enclosing block could cause conflicts even if they are not available at the location.
            // E.g. symbols inside if statements / try catch statements.
            var symbolsInBlock = semanticModel.GetExistingSymbols(container, cancellationToken,
                descendInto: n => ShouldDescendInto(n));

            return symbolsInBlock.Concat(visibleSymbols);

            // Walk through the enclosing block symbols, but avoid exploring local functions
            //     a) Visible symbols from the local function would be returned by LookupSymbols
            //        (e.g. location is inside a local function, the local function method name).
            //     b) Symbols declared inside the local function do not cause collisions with symbols declared outside them, so avoid considering those symbols.
            // Exclude lambdas as well when the language version is C# 8 or higher because symbols declared inside no longer collide with outer variables.
            bool ShouldDescendInto(SyntaxNode node)
            {
                var isLanguageVersionGreaterOrEqualToCSharp8 = (semanticModel.Compilation as CSharpCompilation)?.LanguageVersion >= LanguageVersion.CSharp8;
                return isLanguageVersionGreaterOrEqualToCSharp8 ? !SyntaxFacts.IsAnonymousOrLocalFunction(node) : !SyntaxFacts.IsLocalFunctionStatement(node);
            }
        }

        public bool IsExpressionContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
        {
            return semanticModel.SyntaxTree.IsExpressionContext(
                position,
                semanticModel.SyntaxTree.FindTokenOnLeftOfPosition(position, cancellationToken),
                attributes: true, cancellationToken: cancellationToken, semanticModelOpt: semanticModel);
        }

        public bool IsStatementContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
        {
            return semanticModel.SyntaxTree.IsStatementContext(
                position, semanticModel.SyntaxTree.FindTokenOnLeftOfPosition(position, cancellationToken), cancellationToken);
        }

        public bool IsTypeContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
            => semanticModel.SyntaxTree.IsTypeContext(position, cancellationToken, semanticModel);

        public bool IsNamespaceContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
            => semanticModel.SyntaxTree.IsNamespaceContext(position, cancellationToken, semanticModel);

        public bool IsNamespaceDeclarationNameContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
            => semanticModel.SyntaxTree.IsNamespaceDeclarationNameContext(position, cancellationToken);

        public bool IsTypeDeclarationContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
        {
            return semanticModel.SyntaxTree.IsTypeDeclarationContext(
                position, semanticModel.SyntaxTree.FindTokenOnLeftOfPosition(position, cancellationToken), cancellationToken);
        }

        public bool IsMemberDeclarationContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
        {
            return semanticModel.SyntaxTree.IsMemberDeclarationContext(
                position, semanticModel.SyntaxTree.FindTokenOnLeftOfPosition(position, cancellationToken));
        }

        public bool IsGlobalStatementContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
            => semanticModel.SyntaxTree.IsGlobalStatementContext(position, cancellationToken);

        public bool IsLabelContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
            => semanticModel.SyntaxTree.IsLabelContext(position, cancellationToken);

        public bool IsAttributeNameContext(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
            => semanticModel.SyntaxTree.IsAttributeNameContext(position, cancellationToken);

        public CommonConversion ClassifyConversion(SemanticModel semanticModel, SyntaxNode expression, ITypeSymbol destination)
            => semanticModel.ClassifyConversion((ExpressionSyntax)expression, destination).ToCommonConversion();

#nullable enable

        public IMethodSymbol? TryGetDisposeMethod(SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken)
        {
            var isAsync = false;
            ExpressionSyntax? expression = null;

            if (node is UsingStatementSyntax usingStatement)
            {
                isAsync = usingStatement.AwaitKeyword != default;
                expression = usingStatement is { Declaration.Variables: [{ Initializer: var initializer }] } ? initializer?.Value : usingStatement.Expression;
            }
            else if (node is LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer: var initializer }] } localDeclaration)
            {
                isAsync = localDeclaration.AwaitKeyword != default;
                expression = initializer?.Value;
            }

            if (expression is null)
                return null;

            var type = semanticModel.GetTypeInfo(expression, cancellationToken).Type;
            if (type is null)
                return null;

            var methodToLookFor = isAsync
                ? GetDisposeMethod(typeof(IAsyncDisposable).FullName, nameof(IAsyncDisposable.DisposeAsync))
                : GetDisposeMethod(typeof(IDisposable).FullName, nameof(IDisposable.Dispose));
            if (methodToLookFor is null)
                return null;

            var impl = type.FindImplementationForInterfaceMember(methodToLookFor);
            return impl as IMethodSymbol;

            IMethodSymbol? GetDisposeMethod(string typeName, string methodName)
            {
                var disposableType = semanticModel.Compilation.GetBestTypeByMetadataName(typeof(IAsyncDisposable).FullName);
                return disposableType?.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0 && m.Name == methodName);
            }
        }
    }
}

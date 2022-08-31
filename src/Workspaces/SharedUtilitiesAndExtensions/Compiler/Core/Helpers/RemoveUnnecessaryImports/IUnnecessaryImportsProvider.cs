﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Threading;

namespace Microsoft.CodeAnalysis.RemoveUnnecessaryImports
{
    internal interface IUnnecessaryImportsProvider
    {
        ImmutableArray<SyntaxNode> GetUnnecessaryImports(SemanticModel model, CancellationToken cancellationToken);

        ImmutableArray<SyntaxNode> GetUnnecessaryImports(
            SemanticModel model,
            Func<SyntaxNode, bool>? predicate,
            CancellationToken cancellationToken);
    }
}

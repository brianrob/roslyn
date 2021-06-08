﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.UseIsNullCheck
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    internal sealed class CSharpUseIsNullCheckOverIsObjectDiagnosticAnalyzer : AbstractBuiltInCodeStyleDiagnosticAnalyzer
    {
        public CSharpUseIsNullCheckOverIsObjectDiagnosticAnalyzer()
            : base(IDEDiagnosticIds.UseIsNullOverIsObjectDiagnosticId,
                   EnforceOnBuildValues.UseIsNullCheck,
                   CSharpCodeStyleOptions.PreferIsNullCheckOverIsObject,
                   CSharpAnalyzersResources.Use_is_null_check,
                   new LocalizableResourceString(nameof(CSharpAnalyzersResources.Null_check_can_be_clarified), AnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources)))
        {
        }

        public override DiagnosticAnalyzerCategory GetAnalyzerCategory()
            => DiagnosticAnalyzerCategory.SemanticSpanAnalysis;

        protected override void InitializeWorker(AnalysisContext context)
        {
            context.RegisterCompilationStartAction(context =>
            {
                // All trees should have the same language version. Bail-out early in compilation start instead of checking every tree.
                var tree = context.Compilation.SyntaxTrees.FirstOrDefault();
                if (tree is null || ((CSharpParseOptions)tree.Options).LanguageVersion < LanguageVersion.CSharp9)
                {
                    return;
                }

                context.RegisterOperationAction(c => AnalyzeOperation(c), OperationKind.IsType, OperationKind.NegatedPattern);
            });
        }

        private void AnalyzeOperation(OperationAnalysisContext context)
        {
            var option = context.Options.GetOption(CSharpCodeStyleOptions.PreferIsNullCheckOverIsObject, context.Operation.Syntax.SyntaxTree, context.CancellationToken);
            if (!option.Value)
            {
                return;
            }

            if (ShouldReportDiagnostic(context.Operation))
            {
                var severity = option.Notification.Severity;
                context.ReportDiagnostic(
                    DiagnosticHelper.Create(
                        Descriptor, context.Operation.Syntax.GetLocation(), severity, additionalLocations: null, properties: null));
            }
        }

        private static bool ShouldReportDiagnostic(IOperation operation)
        {
            if (operation is IIsTypeOperation isTypeOperation)
            {
                // Matches 'x is MyType'
                // isTypeOperation.TypeOperand is 'MyType'
                // isTypeOperation.ValueOperand.Type is the type of 'x'.
                return isTypeOperation.ValueOperand.Type is not null &&
                    isTypeOperation.ValueOperand.Type.InheritsFromOrEquals(isTypeOperation.TypeOperand);
            }
            else if (operation is INegatedPatternOperation negatedPattern)
            {
                // Matches 'x is not MyType'
                // InputType is the type of 'x'
                // MatchedType is 'MyType'
                return negatedPattern.Pattern is ITypePatternOperation typePatternOperation &&
                    typePatternOperation.InputType.InheritsFromOrEquals(typePatternOperation.MatchedType);
            }

            // Only OperationKind.IsType and OperationKind.NegatedPattern are registered.
            throw ExceptionUtilities.Unreachable;
        }
    }
}

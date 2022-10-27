﻿/*
 * SonarOmnisharp
 * Copyright (C) 2021-2022 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.Extensions.Logging;
using OmniSharp;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Models.V2;
using OmniSharp.Models.V2.CodeActions;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.Diagnostics;
using OmniSharp.Roslyn.CSharp.Services.Refactoring.V2;
using SonarLint.OmniSharp.DotNet.Services.DiagnosticWorker;
using SonarLint.OmniSharp.DotNet.Services.DiagnosticWorker.AdditionalLocations;
using SonarLint.OmniSharp.DotNet.Services.DiagnosticWorker.QuickFixes;

namespace SonarLint.OmniSharp.DotNet.Services.Services
{
    [OmniSharpEndpoint(SonarLintCodeCheckWithQuickFixesService2.ServiceEndpoint, typeof(SonarLintCodeCheckWithQuickFixesRequest2), typeof(SonarLintCodeCheckWithQuickFixesResponse2))]
    internal class SonarLintCodeCheckWithQuickFixesRequest2 : Request
    {
    }

    internal class SonarLintCodeCheckWithQuickFixesResponse2 : IAggregateResponse
    {
        public IEnumerable<SonarLintDiagnosticLocation> QuickFixes { get; set; }
        public IAggregateResponse Merge(IAggregateResponse response)
        {
            var quickFixResponse = (SonarLintCodeCheckWithQuickFixesResponse)response;
            return new SonarLintCodeCheckWithQuickFixesResponse{ QuickFixes = QuickFixes.Concat(quickFixResponse.QuickFixes)};
        }
    }

    [OmniSharpHandler(ServiceEndpoint, LanguageNames.CSharp)]
    internal class SonarLintCodeCheckWithQuickFixesService2 : BaseCodeActionService<SonarLintCodeCheckWithQuickFixesRequest2, SonarLintCodeCheckWithQuickFixesResponse2>
    {
        internal const string ServiceEndpoint = "/sonarlint/codecheckwithfixes2";

        private readonly ISonarLintDiagnosticWorker diagnosticWorker;
        private readonly IDiagnosticsToCodeLocationsConverter diagnosticsToCodeLocationsConverter;

        [ImportingConstructor]
        public SonarLintCodeCheckWithQuickFixesService2(
            OmniSharpWorkspace workspace,
            [ImportMany] IEnumerable<ISonarAnalyzerCodeActionProvider> providers,
            ILoggerFactory loggerFactory,
            ISonarLintDiagnosticWorker diagnostics,
            CachingCodeFixProviderForProjects codeFixesForProjects,
            OmniSharpOptions options)
            : base(workspace, providers, loggerFactory.CreateLogger<QuickFixesService>(), diagnostics, codeFixesForProjects, options)
        {
            this.diagnosticWorker = diagnostics;
            this.diagnosticsToCodeLocationsConverter = new DiagnosticsToCodeLocationsConverter();
        }

        public override async Task<SonarLintCodeCheckWithQuickFixesResponse2> Handle(SonarLintCodeCheckWithQuickFixesRequest2 request)
        {
            if (string.IsNullOrEmpty(request.FileName))
            {
                return await GetResponseFromDiagnostics(fileName: null);
            }

            return await GetResponseFromDiagnostics(request.FileName);
        }

        private async Task<SonarLintCodeCheckWithQuickFixesResponse2> GetResponseFromDiagnostics(string fileName)
        {
            var diagnosticsWithProjects = await diagnosticWorker.GetDiagnostics(ImmutableArray.Create(fileName));
            var diagnostics = diagnosticsWithProjects.SelectMany(x => x.Diagnostics);
            
            // To produce a complete list of code actions for the document wait until all projects are loaded.
            var document = await this.Workspace.GetDocumentFromFullProjectModelAsync(fileName);
            // if (document == null)
            // {
            //     return Array.Empty<AvailableCodeAction>();
            // }

            var codeActionsPerDiagnostic = new Dictionary<Diagnostic, List<CodeAction>>();
            var method = GetType().BaseType.GetMethod("AppendFixesAsync", BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var diagnostic in diagnostics)
            {
                codeActionsPerDiagnostic[diagnostic] = new List<CodeAction>();
                
                var diagnosticSpan = diagnostic.Location.SourceSpan;
                
                var awaitable = (Task) method.Invoke(this,
                    new object[]
                    {
                        document,
                        diagnosticSpan,
                        new[] {diagnostic},
                        codeActionsPerDiagnostic[diagnostic]
                    });

                await awaitable;
            }

            var diagnosticLocations = diagnosticsToCodeLocationsConverter.Convert(diagnosticsWithProjects, fileName);
            
            foreach (SonarLintDiagnosticLocation diagnosticLocation in diagnosticLocations)
            {
                await AddCodeFixesToDiagnosticLocation(diagnosticLocation, fileName, codeActionsPerDiagnostic[diagnosticLocation.OriginalDiagnostic]);
            }

            return new SonarLintCodeCheckWithQuickFixesResponse2 { QuickFixes = diagnosticLocations };
        }

        private async Task AddCodeFixesToDiagnosticLocation(SonarLintDiagnosticLocation diagnosticLocation, string fileName, IList<CodeAction> codeActions)
        {
            foreach (var availableCodeAction in codeActions)
            {
                var codeFix = await GetCodeFix(availableCodeAction, fileName);
                diagnosticLocation.CodeFixes.Add(codeFix);
            }
        }
        
        private async Task<CodeFix> GetCodeFix(CodeAction availableCodeAction, string fileName)
        {
            var fixes = new List<Fix>();
            try
            {
                var operations = await availableCodeAction.GetOperationsAsync(CancellationToken.None);

                var solution = this.Workspace.CurrentSolution;
                var directory = Path.GetDirectoryName(fileName);

                foreach (var o in operations)
                {
                    if (o is ApplyChangesOperation applyChangesOperation)
                    {
                        var fileChangesResult = await GetFileChangesAsync(applyChangesOperation.ChangedSolution, solution, directory, true, false);

                        Debug.Assert(fileChangesResult.FileChanges.All(c => c is ModifiedFileResponse));

                        var fileFixes = fileChangesResult.FileChanges.Select(c => ((ModifiedFileResponse)c).ToFix());

                        fixes.AddRange(fileFixes);
                        solution = fileChangesResult.Solution;
                    }
                    else
                    {
                        o.Apply(this.Workspace, CancellationToken.None);
                        solution = this.Workspace.CurrentSolution;
                    }
                }
            }
            catch (Exception e)
            {
               Logger.LogError(e, $"An error occurred when running a code action: {availableCodeAction.Title}");
            }

            return new CodeFix
            {
                Message = availableCodeAction.Title,
                Fixes = fixes
            };
        }
    }
}

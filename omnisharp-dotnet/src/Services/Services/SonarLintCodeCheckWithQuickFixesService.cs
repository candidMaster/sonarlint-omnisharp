/*
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
using System.IO;
using System.Linq;
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

namespace SonarLint.OmniSharp.DotNet.Services.Services
{
    [OmniSharpEndpoint(SonarLintCodeCheckWithQuickFixesService.ServiceEndpoint, typeof(SonarLintCodeCheckWithQuickFixesRequest), typeof(SonarLintCodeCheckWithQuickFixesResponse))]
    internal class SonarLintCodeCheckWithQuickFixesRequest : Request
    {
    }

    internal class SonarLintCodeCheckWithQuickFixesResponse : IAggregateResponse
    {
        public IEnumerable<SonarLintDiagnosticLocation> QuickFixes { get; set; }
        public IAggregateResponse Merge(IAggregateResponse response)
        {
            var quickFixResponse = (SonarLintCodeCheckWithQuickFixesResponse)response;
            return new SonarLintCodeCheckWithQuickFixesResponse{ QuickFixes = QuickFixes.Concat(quickFixResponse.QuickFixes)};
        }
    }

    [OmniSharpHandler(ServiceEndpoint, LanguageNames.CSharp)]
    internal class SonarLintCodeCheckWithQuickFixesService : BaseCodeActionService<SonarLintCodeCheckWithQuickFixesRequest, SonarLintCodeCheckWithQuickFixesResponse>
    {
        internal const string ServiceEndpoint = "/sonarlint/codecheckwithfixes";

        private readonly ISonarLintDiagnosticWorker diagnosticWorker;
        private readonly IDiagnosticsToCodeLocationsConverter diagnosticsToCodeLocationsConverter;

        [ImportingConstructor]
        public SonarLintCodeCheckWithQuickFixesService(
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

        public override async Task<SonarLintCodeCheckWithQuickFixesResponse> Handle(SonarLintCodeCheckWithQuickFixesRequest request)
        {
            if (string.IsNullOrEmpty(request.FileName))
            {
                var allDiagnostics = await diagnosticWorker.GetAllDiagnosticsAsync();

                return await GetResponseFromDiagnostics(allDiagnostics, fileName: null);
            }

            var diagnostics = await diagnosticWorker.GetDiagnostics(ImmutableArray.Create(request.FileName));

            return await GetResponseFromDiagnostics(diagnostics, request.FileName);
        }


        private async Task<SonarLintCodeCheckWithQuickFixesResponse> GetResponseFromDiagnostics(ImmutableArray<DocumentDiagnostics> diagnostics, string fileName)
        {
            var diagnosticLocations = diagnosticsToCodeLocationsConverter.Convert(diagnostics, fileName);

            await AddCodeFixesToDiagnosticLocations(diagnosticLocations, fileName);

            return new SonarLintCodeCheckWithQuickFixesResponse { QuickFixes = diagnosticLocations };
        }

        private async Task AddCodeFixesToDiagnosticLocations(IEnumerable<SonarLintDiagnosticLocation> diagnosticLocations, string fileName)
        {
            foreach (var diagnosticLocation in diagnosticLocations)
            {
                await AddCodeFixesToDiagnosticLocation(diagnosticLocation, fileName);
            }
        }

        private async Task AddCodeFixesToDiagnosticLocation(SonarLintDiagnosticLocation diagnosticLocation, string fileName)
        {
            var availableCodeActions = await GetCodeActionIdentifierFromDiagnostic(diagnosticLocation);
            foreach (var availableCodeAction in availableCodeActions)
            {
                var codeFix = await GetCodeFix(availableCodeAction, fileName);
                diagnosticLocation.CodeFixes.Add(codeFix);
            }
        }

        private async Task<IEnumerable<AvailableCodeAction>> GetCodeActionIdentifierFromDiagnostic(SonarLintDiagnosticLocation diagnosticLocation)
        {
            var getCodActionRequest = new GetCodeActionsRequest
            {
                FileName = diagnosticLocation.FileName,
                Selection = new Range
                {
                    Start = new Point { Column = diagnosticLocation.Column, Line = diagnosticLocation.Line },
                    End = new Point { Column = diagnosticLocation.EndColumn, Line = diagnosticLocation.EndLine }
                }
            };

            var availableActions = await GetAvailableCodeActions(getCodActionRequest);

            return availableActions;
        }
        private async Task<RunCodeActionResponse> GetCodeFix(AvailableCodeAction availableCodeAction, string fileName)
        {
            var changes = new List<FileOperationResponse>();
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

                        changes.AddRange(fileChangesResult.FileChanges);
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
                Logger.LogError(e, $"An error occurred when running a code action: {availableCodeAction.GetTitle()}");
            }

            return new RunCodeActionResponse
            {
                Changes = changes
            };
        }
    }
}

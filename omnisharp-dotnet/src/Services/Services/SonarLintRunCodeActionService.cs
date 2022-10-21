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
using OmniSharp.Models.V2.CodeActions;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.CodeActions;
using OmniSharp.Roslyn.CSharp.Services.Refactoring.V2;
using OmniSharp.Services;
using SonarLint.OmniSharp.DotNet.Services.DiagnosticWorker;

namespace SonarLint.OmniSharp.DotNet.Services.Services
{
    /// <summary>
    /// Copy of <see cref="RunCodeActionRequest"/>.
    /// We need to create a new Request object so that we could declare the endpoint.
    /// </summary>
    [OmniSharpEndpoint(SonarLintRunCodeActionService.ServiceEndpoint, typeof(SonarLintRunCodeActionServiceRequest), typeof(RunCodeActionResponse))]
    internal class SonarLintRunCodeActionServiceRequest : RunCodeActionRequest
    {

    }

    /// <summary>
    /// Copy of <see cref="RunCodeActionService"/>.
    /// Changes:
    ///     - Passing <see cref="ISonarLintDiagnosticWorker"/>
    ///     - Passing <see cref="ISonarAnalyzerCodeActionProvider"/>
    /// </summary>
    [OmniSharpHandler(ServiceEndpoint, LanguageNames.CSharp)]
    internal class SonarLintRunCodeActionService: BaseCodeActionService<SonarLintRunCodeActionServiceRequest, RunCodeActionResponse>
    {
        internal const string ServiceEndpoint = "/sonarlint/runcodeaction";

        [ImportingConstructor]
        public SonarLintRunCodeActionService(
            IAssemblyLoader loader,
            OmniSharpWorkspace workspace,
            CodeActionHelper helper,
            [ImportMany] IEnumerable<ISonarAnalyzerCodeActionProvider> providers,
            ILoggerFactory loggerFactory,
            ISonarLintDiagnosticWorker diagnostics,
            CachingCodeFixProviderForProjects codeFixesForProjects,
            OmniSharpOptions options)
            : base(workspace, providers, loggerFactory.CreateLogger<RunCodeActionService>(), diagnostics, codeFixesForProjects, options)
        {
        }

        public override async Task<RunCodeActionResponse> Handle(SonarLintRunCodeActionServiceRequest request)
        {
            var availableActions = await GetAvailableCodeActions(request);
            var availableAction = availableActions.FirstOrDefault(a => a.GetIdentifier().Equals(request.Identifier));
            if (availableAction == null)
            {
                return new RunCodeActionResponse();
            }

            Logger.LogInformation($"Applying code action: {availableAction.GetTitle()}");
            var changes = new List<FileOperationResponse>();

            try
            {
                var operations = await availableAction.GetOperationsAsync(CancellationToken.None);

                var solution = this.Workspace.CurrentSolution;
                var directory = Path.GetDirectoryName(request.FileName);

                foreach (var o in operations)
                {
                    if (o is ApplyChangesOperation applyChangesOperation)
                    {
                        var fileChangesResult = await GetFileChangesAsync(applyChangesOperation.ChangedSolution, solution, directory, request.WantsTextChanges, request.WantsAllCodeActionOperations);

                        changes.AddRange(fileChangesResult.FileChanges);
                        solution = fileChangesResult.Solution;
                    }
                    else
                    {
                        o.Apply(this.Workspace, CancellationToken.None);
                        solution = this.Workspace.CurrentSolution;
                    }

                    if (request.WantsAllCodeActionOperations)
                    {
                        if (o is OpenDocumentOperation openDocumentOperation)
                        {
                            var document = solution.GetDocument(openDocumentOperation.DocumentId);
                            changes.Add(new OpenFileResponse(document.FilePath));
                        }
                    }
                }

                if (request.ApplyTextChanges)
                {
                    // Will this fail if FileChanges.GetFileChangesAsync(...) added files to the workspace?
                    this.Workspace.TryApplyChanges(solution);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"An error occurred when running a code action: {availableAction.GetTitle()}");
            }

            return new RunCodeActionResponse
            {
                Changes = changes
            };
        }
    }
}

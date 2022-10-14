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

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using OmniSharp;
using OmniSharp.Mef;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OmniSharp.Models.V2.CodeActions;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.Refactoring.V2;
using SonarLint.OmniSharp.DotNet.Services.DiagnosticWorker;

namespace SonarLint.OmniSharp.DotNet.Services.Services
{
    /// <summary>
    /// Copy of <see cref="GetCodeActionsRequest"/>.
    /// We need to create a new Request object so that we could declare the endpoint.
    /// </summary>
    [OmniSharpEndpoint(QuickFixesService.ServiceEndpoint, typeof(SonarLintGetFixesRequest), typeof(GetCodeActionsResponse))]
    internal class SonarLintGetFixesRequest : GetCodeActionsRequest
    {
    }
    
    /// <summary>
    /// Copy of <see cref="GetCodeActionsService"/>.
    /// Changes:
    ///     - Passing <see cref="ISonarLintDiagnosticWorker"/>
    ///     - Passing <see cref="ISonarAnalyzerCodeActionProvider"/>
    /// </summary>
    [OmniSharpHandler(ServiceEndpoint, LanguageNames.CSharp)]
    internal class QuickFixesService : BaseCodeActionService<SonarLintGetFixesRequest, GetCodeActionsResponse>
    {
        internal const string ServiceEndpoint = "/sonarlint/fixes";
        
        [ImportingConstructor]
        public QuickFixesService(
            OmniSharpWorkspace workspace,
            [ImportMany] IEnumerable<ISonarAnalyzerCodeActionProvider> providers,
            ILoggerFactory loggerFactory,
            ISonarLintDiagnosticWorker diagnostics,
            CachingCodeFixProviderForProjects codeFixesForProjects,
            OmniSharpOptions options)
            : base(workspace, providers, loggerFactory.CreateLogger<QuickFixesService>(), diagnostics, codeFixesForProjects, options)
        {
        }

        public override async Task<GetCodeActionsResponse> Handle(SonarLintGetFixesRequest request)
        {
            var availableActions = await GetAvailableCodeActions(request);

            return new GetCodeActionsResponse
            {
                CodeActions = availableActions.Select(ConvertToOmniSharpCodeAction)
            };
        }

        private static OmniSharpCodeAction ConvertToOmniSharpCodeAction(AvailableCodeAction availableAction)
        {
            return new OmniSharpCodeAction(availableAction.GetIdentifier(), availableAction.GetTitle());
        }
    }
}

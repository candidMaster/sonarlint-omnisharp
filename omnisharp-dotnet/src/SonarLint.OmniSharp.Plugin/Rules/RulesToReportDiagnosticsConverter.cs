﻿/*
 * SonarOmnisharp
 * Copyright (C) 2021-2021 SonarSource SA
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
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SonarLint.OmniSharp.Plugin.Rules
{
    internal interface IRulesToReportDiagnosticsConverter
    {
        Dictionary<string, ReportDiagnostic> Convert(IEnumerable<RuleDefinition> rules);
    }

    internal class RulesToReportDiagnosticsConverter : IRulesToReportDiagnosticsConverter
    {
        public Dictionary<string, ReportDiagnostic> Convert(IEnumerable<RuleDefinition> rules)
        {
            // the severity is handled on the java side; we're using 'warn' just to make sure that the rule is run. 
            var diagnosticOptions = rules
                .ToDictionary(x => x.RuleId,
                    rule => rule.IsEnabled
                        ? ReportDiagnostic.Warn
                        : ReportDiagnostic.Suppress);

            return diagnosticOptions;
        }
    }
}
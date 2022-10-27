using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OmniSharp.Models;

namespace SonarLint.OmniSharp.DotNet.Services.DiagnosticWorker.QuickFixes
{
    internal static class ModifiedFileResponseExtensions
    {
        public static Fix ToFix(this ModifiedFileResponse modifiedFileResponse)
        {
            return new Fix
            {
                FileName = modifiedFileResponse.FileName,
                Edits = modifiedFileResponse.Changes.Select(c=> c.ToEdit())
            };
        }

        public static Edit ToEdit(this LinePositionSpanTextChange textChange)
        {
            return new Edit
            {
                Text = textChange.NewText,
                StartLine = textChange.StartLine,
                EndLine = textChange.EndLine,
                StartColumn = textChange.StartColumn,
                EndColumn = textChange.EndColumn
                
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace SonarLint.OmniSharp.DotNet.Services.DiagnosticWorker.QuickFixes
{
    internal class Fix
    {
        public string FileName { get; set; }
        public IEnumerable<Edit> Edits { get; set; }
    }
}

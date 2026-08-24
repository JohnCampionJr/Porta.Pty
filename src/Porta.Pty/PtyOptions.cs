// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Options for spawning a new pty process.
    /// </summary>
    public class PtyOptions
    {
        /// <summary>
        /// Gets or sets the terminal name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the number of initial rows.
        /// </summary>
        public int Rows { get; set; }

        /// <summary>
        /// Gets or sets the number of initial columns.
        /// </summary>
        public int Cols { get; set; }

        /// <summary>
        /// Gets or sets the working directory for the spawned process.
        /// </summary>
        public string Cwd { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the path to the process to be spawned.
        /// </summary>
        public string App { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the command line arguments to the process.
        /// </summary>
        public string[] CommandLine { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets a value indicating whether command line arguments must be quoted.
        /// <c>false</c>, the default, means that the arguments must be quoted and quotes inside escaped then concatenated with spaces.
        /// <c>true</c> means that the arguments must not be quoted and just concatenated with spaces.
        /// </summary>
        public bool VerbatimCommandLine { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this library answers ConPTY's startup Primary
        /// Device Attributes query on the consumer's behalf. <c>true</c>, the default, is what a
        /// consumer that is not a terminal wants. A TERMINAL EMULATOR should set this to
        /// <see langword="false"/> and answer for itself.
        /// </summary>
        /// <remarks>
        /// <para>Out-of-band ConPTY opens by asking the terminal what it is (<c>ESC[c</c>) and blocks
        /// in <c>WaitUntilDA1(3000)</c> for the reply. A consumer that only READS never answers, so it
        /// pays that timeout on every pseudoconsole: measured from spawn to the child's first output,
        /// <c>3088ms</c> and <c>3097ms</c> unanswered against <c>109ms</c> answered. Defaulting to
        /// <see langword="true"/> keeps that cost off consumers who have no reason to know what DA1
        /// even is — task runners, log readers, test harnesses.</para>
        ///
        /// <para>But the reply this library sends is a canned "VT100 with Advanced Video Option",
        /// which is a claim about a terminal it knows nothing about. A consumer that IS a terminal can
        /// state its real capabilities, and should: set this <see langword="false"/> and the query is
        /// left in the output for the emulator to answer. Measured at <c>92ms</c> to first output, so
        /// nothing is paid for the privilege.</para>
        ///
        /// <para>Answering is one half of a pair. While this is <see langword="true"/> the query is
        /// also REMOVED from the output, because a consumer that answered a question we had already
        /// answered would have its reply delivered to the child as keyboard input — an interactive
        /// shell echoes it, and <c>^[[?1;2c</c> appears beside the first prompt. Setting this
        /// <see langword="false"/> stops both: we neither answer nor hide.</para>
        ///
        /// <para>Only a DA1 query from ConPTY's own startup is involved either way. A query from the
        /// CHILD — a program asking what terminal it is running under — is always forwarded, whatever
        /// this is set to.</para>
        ///
        /// <para><b>Windows only, and only on the out-of-band ConPTY path.</b> In-box ConPTY does not
        /// ask, and Unix has no such handshake, so there is nothing to answer and nothing to hide.</para>
        /// </remarks>
        public bool AnswerDeviceAttributes { get; set; } = true;

        /// <summary>
        /// Gets or sets the process' environment variables.
        /// </summary>
        public IDictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
    }
}

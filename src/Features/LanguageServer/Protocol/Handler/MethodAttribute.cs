﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

[MetadataAttribute]
internal class MethodAttribute : LanguageServerEndpointAttribute
{
    public MethodAttribute(string method, string? language = null) : base(method, language)
    {
    }
}

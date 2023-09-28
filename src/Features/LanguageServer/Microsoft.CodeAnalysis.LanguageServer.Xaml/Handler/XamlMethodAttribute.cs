﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.Handler;

namespace Microsoft.CodeAnalysis.LanguageServer.Xaml.Handler
{
    internal class XamlMethodAttribute : MethodAttribute
    {
        public XamlMethodAttribute(string method) : base(method, StringConstants.XamlLanguageName)
        {
        }
    }
}

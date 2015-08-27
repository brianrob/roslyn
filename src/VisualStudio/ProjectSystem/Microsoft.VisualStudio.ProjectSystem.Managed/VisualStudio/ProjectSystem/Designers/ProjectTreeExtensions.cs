﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.VisualStudio.ProjectSystem.Utilities.Designers;

namespace Microsoft.VisualStudio.ProjectSystem.Designers
{
    /// <summary>
    ///     Provides extension methods for <see cref="IProjectTree"/> instances.
    /// </summary>
    internal static class ProjectTreeExtensions
    {
        /// <summary>
        ///     Returns a value indicating whether the specified <see cref="IProjectTree"/> is
        ///     the project root; that is, has the capability <see cref="ProjectTreeCapabilities.ProjectRoot"/>.
        /// </summary>
        public static bool IsProjectRoot(this IProjectTree tree)
        {
            Requires.NotNull(tree, nameof(tree));

            return tree.HasCapability(ProjectTreeCapabilities.ProjectRoot);
        }

        /// <summary>
        ///     Returns a value indicating whether the specified <see cref="IProjectTree"/> has
        ///     the specified capability.
        /// </summary>
        public static bool HasCapability(this IProjectTree tree, string capability)
        {
            Requires.NotNull(tree, nameof(tree));

            return tree.Capabilities.Contains(capability); 
        }
    }
}

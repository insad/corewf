﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Reflection;

namespace Test.Common.TestObjects.Utilities
{
    public static class PartialTrustMethodInfo
    {
        // [ReflectionPermission(SecurityAction.Assert, MemberAccess = true)]
        public static object Invoke(MethodInfo methodInfo, object obj, object[] parameters)
        {
            return methodInfo.Invoke(obj, parameters);
        }

        // [ReflectionPermission(SecurityAction.Assert, MemberAccess = true)]
        //public static object Invoke(MethodInfo methodInfo, object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture)
        //{
        //    return methodInfo.Invoke(obj, invokeAttr, binder, parameters, culture);
        //}
    }
}

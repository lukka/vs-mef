﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Composition;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Remoting.Messaging;
    using System.Runtime.Remoting.Proxies;
    using System.Text;
    using System.Threading.Tasks;
    using Validation;

    /// <summary>
    /// Extension that adds support for dynamic interface metadata view generation.
    /// </summary>
    public static class ExportMetadataViewInterfaceProxy
    {
        private static readonly MethodInfo EqualsMethodInfo = typeof(object).GetMethod("Equals", BindingFlags.Instance | BindingFlags.Public);

        private static readonly ComposablePartDefinition proxySupportPartDefinition = new AttributedPartDiscovery().CreatePart(typeof(MetadataViewProxyProvider));

        /// <summary>
        /// Adds support for queries to <see cref="ExportProvider.GetExports{T, TMetadata}"/> where
        /// <c>TMetadata</c> is an interface.
        /// </summary>
        /// <param name="catalog">The catalog from which constructed ExportProviders may have this support added.</param>
        /// <returns>The catalog with the additional support.</returns>
        public static ComposableCatalog WithMetadataViewProxySupport(this ComposableCatalog catalog)
        {
            Requires.NotNull(catalog, "catalog");

            return catalog.WithPart(proxySupportPartDefinition);
        }

        private static TMetadata GetProxy<TMetadata>(IReadOnlyDictionary<string, object> metadata)
        {
            Requires.NotNull(metadata, "metadata");

            var proxy = new MetadataProxy<TMetadata>(metadata);
            return (TMetadata)proxy.GetTransparentProxy();
        }

        private class MetadataProxy<TMetadata> : RealProxy
        {
            private readonly IReadOnlyDictionary<string, object> metadata;
            private object transparentProxy;

            internal MetadataProxy(IReadOnlyDictionary<string, object> metadata)
                : base(typeof(TMetadata))
            {
                Requires.NotNull(metadata, "metadata");
                this.metadata = metadata;
            }

            public override object GetTransparentProxy()
            {
                return this.transparentProxy = base.GetTransparentProxy();
            }

            public override IMessage Invoke(IMessage msg)
            {
                var methodCall = (IMethodCallMessage)msg;
                var methodInfo = (MethodInfo)methodCall.MethodBase;

                object result;
                if (methodInfo.GetParameters().Length == 0 && methodInfo.IsSpecialName && methodInfo.Name.StartsWith("get_"))
                {
                    string propertyName = methodCall.MethodName.Substring(4);
                    result = this.metadata[propertyName];
                }
                else if (methodInfo == EqualsMethodInfo)
                {
                    // Specially handle Equals so it returns true appropriately.
                    // In particular, if the caller passed in this proxy as the argument,
                    // substitute in the underlying value so that it recognizes it according to its own equality check.
                    result = methodInfo.Invoke(this, new object[] { methodCall.InArgs[0] == this.transparentProxy ? this : methodCall.InArgs[0] });
                }
                else
                {
                    result = methodInfo.Invoke(this, methodCall.InArgs);
                }

                return new ReturnMessage(result, null, 0, methodCall.LogicalCallContext, methodCall);
            }
        }

        [PartNotDiscoverable]
        [Export(typeof(IMetadataViewProvider))]
        private class MetadataViewProxyProvider : IMetadataViewProvider
        {
            public bool IsDefaultMetadataRequired
            {
                get { return true; }
            }

            public bool IsMetadataViewSupported(Type metadataType)
            {
                if (metadataType.IsInterface &&
                    metadataType.GetMembers().All(IsPropertyRelated))
                {
                    return true;
                }

                return false;
            }

            public TMetadata CreateProxy<TMetadata>(IReadOnlyDictionary<string, object> metadata)
            {
                return GetProxy<TMetadata>(metadata);
            }

            private static bool IsPropertyRelated(MemberInfo member)
            {
                var property = member as PropertyInfo;
                if (property != null)
                {
                    return property.GetMethod != null && property.SetMethod == null;
                }

                var method = member as MethodInfo;
                if (method != null)
                {
                    return method.IsSpecialName
                        && method.Name.StartsWith("get_");
                }

                return false;
            }
        }
    }
}
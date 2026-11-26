using System.Runtime.CompilerServices;
using Unified.Data.Tables;
using Unified.Data.Tables.Providers;

// Until 0.10.0 these public types were declared in this assembly. They now live in
// Unified.Data.Tables.Core (same namespaces, same members), which the in-memory provider references
// instead of this one. The forwards keep code compiled against 0.9.2 binding: a reference to
// "TableEntitySerializer, Unified.Data.Tables" resolves to the one type in Core, so static state such as
// TableEntitySerializer.OversizedCellPolicy stays one value, never a copy per assembly.
[assembly: TypeForwardedTo(typeof(AssemblyQualifiedTypeDiscriminator))]
[assembly: TypeForwardedTo(typeof(CacheExpirationMode))]
[assembly: TypeForwardedTo(typeof(CachePolicy))]
[assembly: TypeForwardedTo(typeof(ITypeDiscriminator))]
[assembly: TypeForwardedTo(typeof(OversizedCellException))]
[assembly: TypeForwardedTo(typeof(OversizedCellPolicy))]
[assembly: TypeForwardedTo(typeof(TableEntitySerializer))]
[assembly: TypeForwardedTo(typeof(TableFilterTranslator))]
[assembly: TypeForwardedTo(typeof(TableRequestSize))]
[assembly: TypeForwardedTo(typeof(TypeDiscriminatorMap))]

// The provider SPI (Unified.Data.Tables.Providers).
[assembly: TypeForwardedTo(typeof(BlobReference))]
[assembly: TypeForwardedTo(typeof(BlobState))]
[assembly: TypeForwardedTo(typeof(IOffloadBlobStore))]
[assembly: TypeForwardedTo(typeof(LargeValueRows))]
[assembly: TypeForwardedTo(typeof(LargeValueTable))]
[assembly: TypeForwardedTo(typeof(LargeValueWritePlan))]
[assembly: TypeForwardedTo(typeof(OffloadBlobNames))]
[assembly: TypeForwardedTo(typeof(ProjectedRows))]
[assembly: TypeForwardedTo(typeof(ProtectedLargeValues))]

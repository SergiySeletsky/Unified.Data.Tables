namespace Unified.Data.Tables.Tests.NeutralFailures;

/// <summary>
/// The provider-neutral failure types of 0.10.0. <see cref="DuplicateKeyException"/>,
/// <see cref="ConcurrencyConflictException"/> and <see cref="BatchPayloadTooLargeException"/> derived
/// from <see cref="Exception"/> directly until then; they now derive from <see cref="StorageException"/>.
/// Inserting a base class is binary-compatible: everything a 0.9.2 caller wrote against them (their
/// constructors, their properties, a catch of the type or of <see cref="Exception"/>) still works.
/// </summary>
public class StorageExceptionTypesTests
{
    public static TheoryData<Type> NeutralTypes =>
    [
        typeof(EntityNotFoundException),
        typeof(TableNotFoundException),
        typeof(StorageRequestRejectedException),
        typeof(TransientStorageException),
        typeof(BatchNotAtomicException),
        typeof(DuplicateKeyException),
        typeof(ConcurrencyConflictException),
        typeof(BatchPayloadTooLargeException),
    ];

    [Theory]
    [MemberData(nameof(NeutralTypes))]
    public void EveryStorageFailure_DerivesDirectlyFromStorageException(Type type)
    {
        Assert.Equal(typeof(StorageException), type.BaseType);
        Assert.Equal(typeof(Exception), typeof(StorageException).BaseType);
    }

    [Theory]
    [MemberData(nameof(NeutralTypes))]
    public void TheNewTypes_AreNotSealed_AndTheOldOnesKeepTheirShape(Type type)
    {
        // BatchPayloadTooLargeException was sealed in 0.9.2 and stays so; nothing else is.
        Assert.Equal(type == typeof(BatchPayloadTooLargeException), type.IsSealed);
        Assert.False(typeof(StorageException).IsSealed);
    }

    [Fact]
    public void TheTypesThatAreNotStorageFailures_AreLeftAlone()
    {
        // Serialization- and usage-shaped failures keep their own bases.
        Assert.False(typeof(StorageException).IsAssignableFrom(typeof(TableValueIntegrityException)));
        Assert.False(typeof(StorageException).IsAssignableFrom(typeof(OversizedValueException)));
    }

    // ── what a 0.9.2 caller wrote still compiles and behaves ───────────────

    [Fact]
    public void DuplicateKeyException_KeepsEveryConstructor_AndItsProperties()
    {
        var inner = new InvalidOperationException("provider");

        Assert.Equal("Duplicate key: a row with this id already exists.", new DuplicateKeyException().Message);
        Assert.Equal("m", new DuplicateKeyException("m").Message);
        Assert.Same(inner, new DuplicateKeyException("m", inner).InnerException);
        var specific = new DuplicateKeyException("Order", "p|r", inner);
        Assert.Equal("Order", specific.EntityType);
        Assert.Equal("p|r", specific.Id);
        Assert.Same(inner, specific.InnerException);
        Assert.Equal("Duplicate key creating Order 'p|r': a row with this id already exists.", specific.Message);
        // The new signal is off unless a provider sets it.
        Assert.False(specific.MayBeOwnWrite);
        Assert.Null(specific.ProviderErrorCode);
    }

    [Fact]
    public void ConcurrencyConflictException_KeepsEveryConstructor_AndItsProperties()
    {
        var inner = new InvalidOperationException("provider");

        Assert.Equal("Concurrency conflict: the row was modified by another writer since it was read.", new ConcurrencyConflictException().Message);
        Assert.Equal("m", new ConcurrencyConflictException("m").Message);
        Assert.Same(inner, new ConcurrencyConflictException("m", inner).InnerException);
        var specific = new ConcurrencyConflictException("Order", "p|r", inner);
        Assert.Equal("Order", specific.EntityType);
        Assert.Equal("p|r", specific.Id);
        Assert.Same(inner, specific.InnerException);
        Assert.Equal("Concurrency conflict updating Order 'p|r': the row was modified by another writer since it was read.", specific.Message);
        Assert.False(specific.MayBeOwnWrite);
    }

    [Fact]
    public void BatchPayloadTooLargeException_KeepsItsConstructor_AndItsProperties()
    {
        var ex = new BatchPayloadTooLargeException(3, 5_000_000, 3_000_000);

        Assert.Equal(3, ex.Index);
        Assert.Equal(5_000_000, ex.Bytes);
        Assert.Equal(3_000_000, ex.Limit);
        Assert.Contains("entity at index 3", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void BatchPlanner_StillThrowsBatchPayloadTooLarge_NowAlsoAStorageException()
    {
        var thrown = Assert.Throws<BatchPayloadTooLargeException>(() => BatchPlanner.Plan([1, 9], maxBytes: 5));

        Assert.IsType<StorageException>(thrown, exactMatch: false);
        Assert.Equal(1, thrown.Index);
    }

    [Fact]
    public void ExistingCatches_StillCatchTheReparentedTypes()
    {
        // The catch blocks 0.9.2 callers have: the type itself, and Exception.
        Assert.Equal("duplicate", Caught(new DuplicateKeyException("Order", "p|r", null)));
        Assert.Equal("conflict", Caught(new ConcurrencyConflictException("Order", "p|r", null)));
        Assert.Equal("too large", Caught(new BatchPayloadTooLargeException(0, 2, 1)));
        Assert.Equal("exception", CaughtAsException(new DuplicateKeyException()));
        Assert.Equal("exception", CaughtAsException(new ConcurrencyConflictException()));
        Assert.Equal("exception", CaughtAsException(new BatchPayloadTooLargeException(0, 2, 1)));
        // And a filter that excludes cancellation, as retry helpers are written.
        Assert.Equal("not cancellation", CaughtUnlessCancelled(new ConcurrencyConflictException()));
    }

    [Fact]
    public void ANewCatchOfStorageException_CatchesEveryStorageFailure_ButNoUsageError()
    {
        foreach (var failure in new StorageException[]
                 {
                     new DuplicateKeyException(), new ConcurrencyConflictException(), new BatchPayloadTooLargeException(0, 2, 1),
                     new EntityNotFoundException(), new TableNotFoundException(), new StorageRequestRejectedException(),
                     new TransientStorageException(), new BatchNotAtomicException(), new StorageException(),
                 })
        {
            Assert.Equal("storage", CaughtAsStorage(failure));
        }
        Assert.Equal("other", CaughtAsStorage(new InvalidOperationException()));
        Assert.Equal("other", CaughtAsStorage(new OperationCanceledException()));
        Assert.Equal("other", CaughtAsStorage(new ArgumentException("x")));
    }

    private static string Caught(Exception toThrow)
    {
        try
        {
            throw toThrow;
        }
        catch (DuplicateKeyException)
        {
            return "duplicate";
        }
        catch (ConcurrencyConflictException)
        {
            return "conflict";
        }
        catch (BatchPayloadTooLargeException)
        {
            return "too large";
        }
    }

    private static string CaughtAsException(Exception toThrow)
    {
        try
        {
            throw toThrow;
        }
        catch (Exception)
        {
            return "exception";
        }
    }

    private static string CaughtUnlessCancelled(Exception toThrow)
    {
        try
        {
            throw toThrow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "not cancellation";
        }
    }

    private static string CaughtAsStorage(Exception toThrow)
    {
        try
        {
            throw toThrow;
        }
        catch (StorageException)
        {
            return "storage";
        }
        catch (Exception)
        {
            return "other";
        }
    }

    // ── the new types ──────────────────────────────────────────────────────

    [Fact]
    public void StorageException_CarriesTheProviderErrorCode_AndTheProviderException()
    {
        var inner = new InvalidOperationException("provider");

        Assert.Equal("The storage request failed.", new StorageException().Message);
        Assert.Equal("m", new StorageException("m").Message);
        Assert.Same(inner, new StorageException("m", inner).InnerException);
        var coded = new StorageException("m", "AuthorizationFailure", inner);
        Assert.Equal("AuthorizationFailure", coded.ProviderErrorCode);
        Assert.Same(inner, coded.InnerException);
        Assert.Null(new StorageException("m").ProviderErrorCode);
    }

    [Fact]
    public void EntityNotFoundException_NamesTheRow()
    {
        var inner = new InvalidOperationException("provider");

        var specific = new EntityNotFoundException("Order", "p|r", inner);

        Assert.Equal("Order", specific.EntityType);
        Assert.Equal("p|r", specific.Id);
        Assert.Same(inner, specific.InnerException);
        Assert.Equal("Order 'p|r' not found: no row exists at this id.", specific.Message);
        Assert.Null(new EntityNotFoundException().Id);
        Assert.Equal("m", new EntityNotFoundException("m").Message);
        Assert.Same(inner, new EntityNotFoundException("m", inner).InnerException);
    }

    [Fact]
    public void TableNotFoundException_NamesTheTable_AndIsNotAMissingEntity()
    {
        var inner = new InvalidOperationException("provider");

        var specific = new TableNotFoundException("Table 'Orders' does not exist.", "Orders", inner);

        Assert.Equal("Orders", specific.TableName);
        Assert.Same(inner, specific.InnerException);
        Assert.Null(new TableNotFoundException().TableName);
        Assert.Equal("m", new TableNotFoundException("m").Message);
        Assert.Same(inner, new TableNotFoundException("m", inner).InnerException);
        Assert.False(typeof(EntityNotFoundException).IsAssignableFrom(typeof(TableNotFoundException)));
        Assert.False(typeof(TableNotFoundException).IsAssignableFrom(typeof(EntityNotFoundException)));
    }

    [Fact]
    public void StorageRequestRejectedException_CarriesTheProviderErrorCode()
    {
        var inner = new InvalidOperationException("provider");

        var rejected = new StorageRequestRejectedException("m", "PropertyValueTooLarge", inner);

        Assert.Equal("PropertyValueTooLarge", rejected.ProviderErrorCode);
        Assert.Same(inner, rejected.InnerException);
        Assert.NotEmpty(new StorageRequestRejectedException().Message);
        Assert.Equal("m", new StorageRequestRejectedException("m").Message);
        Assert.Same(inner, new StorageRequestRejectedException("m", inner).InnerException);
    }

    [Fact]
    public void TransientStorageException_SaysItMayHaveCommitted_UnlessToldOtherwise()
    {
        var inner = new InvalidOperationException("provider");

        // The conservative default: a constructor that is not told assumes the call may have committed.
        Assert.True(new TransientStorageException().MayHaveCommitted);
        Assert.True(new TransientStorageException("m").MayHaveCommitted);
        Assert.True(new TransientStorageException("m", inner).MayHaveCommitted);
        Assert.Null(new TransientStorageException("m", inner).RetryAfter);

        var read = new TransientStorageException("m", mayHaveCommitted: false, TimeSpan.FromSeconds(3), inner);
        Assert.False(read.MayHaveCommitted);
        Assert.Equal(TimeSpan.FromSeconds(3), read.RetryAfter);
        Assert.Same(inner, read.InnerException);
    }

    [Fact]
    public void BatchNotAtomicException_HasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("provider");

        Assert.Contains("nothing was written", new BatchNotAtomicException().Message, StringComparison.Ordinal);
        Assert.Equal("m", new BatchNotAtomicException("m").Message);
        Assert.Same(inner, new BatchNotAtomicException("m", inner).InnerException);
    }
}

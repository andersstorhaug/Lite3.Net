namespace Lite3DotNet.Tests;

/// <remarks>
/// Ported from <c>collisions.c</c>.
/// </remarks>
public class CollisionsTests
{
    private const int TestArrayCount = 1024 * 1024;
    private static readonly byte[] Alphanums = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8.ToArray();
    
    [Fact]
    public void Can_handle_key_hash_collisions()
    {
        var buffer = new byte[1024 * 64];
        var random = new Random(52073821);

        Lite3.InitializeObject(buffer, out var position);

        const int keyLength = 2;
        
        // Array to store random characters, and to find colliding keys
        var keyArray = new byte[TestArrayCount * keyLength].AsSpan();
        
        // Array for storing keys that have been found to collide
        var collidingKeysArray = new byte[keyArray.Length * 2].AsSpan();
        
        // Fill array with pseudo-random alphanumeric characters
        for (var i = 0; i < keyArray.Length; i++)
            keyArray[i] = Alphanums[random.Next(Alphanums.Length)];
        
        // Loop over the key array, try to find colliding keys and store them
        var previousKey = keyArray[..keyLength];
        var previousHash = 0u;
        var collidingKeysAppendArray = collidingKeysArray;
        var collidingKeyCount = 0;
        for (var i = 0; i < keyArray.Length; i += keyLength)
        {
            var currentKey = keyArray.Slice(i, keyLength);
            var keyData = Lite3.GetKeyData(keyArray.Slice(i, keyLength));
            if (previousHash == keyData.Hash && previousKey.SequenceEqual(currentKey))
            {
                Lite3.SetNull(buffer, ref position, 0, previousKey, keyData);
                Lite3.SetNull(buffer, ref position, 0, currentKey, keyData);
                previousKey.CopyTo(collidingKeysAppendArray);
                collidingKeysAppendArray = collidingKeysAppendArray[keyLength..];
                collidingKeyCount++;
            }
            previousHash = keyData.Hash;
            previousKey = currentKey;
        }
        
        // For every key we inserted, can we can actually find it back in the message?
        for (var i = 0; i < collidingKeyCount; i += keyLength)
        {
            var testKey = collidingKeysArray.Slice(i, keyLength);

            Lite3.ContainsKey(buffer, 0, testKey).ShouldBeTrue();
        }
    }
}
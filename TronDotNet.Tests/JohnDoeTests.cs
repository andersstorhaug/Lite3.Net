namespace TronDotNet.Tests;

using TronDotNet;
using static Tron;

/// <remarks>
/// Ported from <c>john_doe.c</c>.
/// </remarks>
public class JohnDoeTests
{
    [Fact]
    public void Can_read_and_write_basic_data()
    {
        Span<byte> buffer = new byte[2048];

        var position = InitializeObject(buffer);

        SetLong(buffer, ref position, 0, "user_id"u8, 12345);
        SetString(buffer, ref position, 0, "username"u8, "jdoe"u8);
        SetString(buffer, ref position, 0, "email_address"u8, "jdoe@example.com"u8);
        SetBool(buffer, ref position, 0, "is_active"u8, true);
        SetDouble(buffer, ref position, 0, "account_balance"u8, 259.75);
        SetString(buffer, ref position, 0, "signup_date_str"u8, "2023-08-15"u8);
        SetString(buffer, ref position, 0, "last_login_date_iso"u8, "2025-09-13T13:20:00Z"u8);
        SetLong(buffer, ref position, 0, "birth_year"u8, 1996);
        SetString(buffer, ref position, 0, "phone_number"u8, "+14155555671"u8);
        SetString(buffer, ref position, 0, "preferred_language"u8, "en"u8);
        SetString(buffer, ref position, 0, "time_zone"u8, "Europe/Berlin"u8);
        SetLong(buffer, ref position, 0, "loyalty_points"u8, 845);
        SetDouble(buffer, ref position, 0, "avg_session_length_minutes"u8, 14.3);
        SetBool(buffer, ref position, 0, "newsletter_subscribed"u8, false);
        SetString(buffer, ref position, 0, "ip_address"u8, "192.168.0.42"u8);
        SetNull(buffer, ref position, 0, "notes"u8);
        
        GetLong(buffer, 0, "user_id"u8).ShouldBe(12345);
        GetString(buffer, 0, "username"u8).GetStringValue(buffer).ShouldBe("jdoe");
        GetString(buffer, 0, "email_address"u8).GetStringValue(buffer).ShouldBe("jdoe@example.com");
        GetBool(buffer, 0, "is_active"u8).ShouldBeTrue();
        GetDouble(buffer, 0, "account_balance"u8).ShouldBe(259.75);
        GetString(buffer, 0, "signup_date_str"u8).GetStringValue(buffer).ShouldBe("2023-08-15");
        GetString(buffer, 0, "last_login_date_iso"u8).GetStringValue(buffer).ShouldBe("2025-09-13T13:20:00Z");
        GetLong(buffer, 0, "birth_year"u8).ShouldBe(1996);
        GetString(buffer, 0, "phone_number"u8).GetStringValue(buffer).ShouldBe("+14155555671");
        GetString(buffer, 0, "preferred_language"u8).GetStringValue(buffer).ShouldBe("en");
        GetString(buffer, 0, "time_zone"u8).GetStringValue(buffer).ShouldBe("Europe/Berlin");
        GetLong(buffer, 0, "loyalty_points"u8).ShouldBe(845);
        GetDouble(buffer, 0, "avg_session_length_minutes"u8).ShouldBe(14.3);
        GetBool(buffer, 0, "newsletter_subscribed"u8).ShouldBeFalse();
        GetString(buffer, 0, "ip_address"u8).GetStringValue(buffer).ShouldBe("192.168.0.42");
    }
}
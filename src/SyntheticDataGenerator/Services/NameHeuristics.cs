namespace SyntheticDataGenerator.Services;

public record NameRule(
    Func<string, bool> Match,
    string GeneratorName,
    Dictionary<string, object?>? Args);

public static class NameHeuristics
{
    public static readonly IReadOnlyList<NameRule> Rules =
    [
        new(n => Helpers.Like(n, "first") && Helpers.Like(n, "name"), "Name.FirstName", null),
        new(n => Helpers.Like(n, "last") && Helpers.Like(n, "name"),  "Name.LastName", null),
        new(n => Helpers.Like(n, "email"),                     "Internet.Email", null),
        new(n => Helpers.Like(n, "phone"),                     "Phone.PhoneNumber", new() { ["format"] = "###-###-####" }),
        new(n => Helpers.Like(n, "street") || (Helpers.Like(n, "address") && !Helpers.Like(n, "email")),
                                                    "Address.StreetAddress", null),
        new(n => Helpers.Like(n, "city"),                      "Address.City", null),
        new(n => Helpers.Like(n, "state"),                     "Address.StateAbbr", null),
        new(n => Helpers.Like(n, "zip") || Helpers.Like(n, "postal"),  "Address.ZipCode", null),
        new(n => Helpers.Like(n, "country"),                   "Address.Country", null),
        new(n => Helpers.Like(n, "url") || Helpers.Like(n, "website"), "Internet.Url", null),
        new(n => Helpers.Like(n, "description") || Helpers.Like(n, "comment") || Helpers.Like(n, "note"),
                                                    "Lorem.Sentence", null),
        new(n => Helpers.Like(n, "price") || Helpers.Like(n, "amount") || Helpers.Like(n, "cost") || Helpers.Like(n, "salary"),
                                                    "Finance.Amount", new() { ["min"] = 1m, ["max"] = 10000m }),
        new(n => Helpers.Like(n, "company"),                   "Company.CompanyName", null),
        new(n => Helpers.Like(n, "title"),                     "Name.JobTitle", null),
        new(n => Helpers.Like(n, "quantity") || Helpers.Like(n, "count") || Helpers.Like(n, "qty"),
                                                    "Random.Int", new() { ["min"] = 1, ["max"] = 100 }),
        new(n => Helpers.Like(n, "status"),                    "PickRandom", new() { ["values"] = new[] { "Active", "Inactive", "Pending" } }),
        new(n => n.StartsWith("is_", StringComparison.OrdinalIgnoreCase)
           || n.StartsWith("has_", StringComparison.OrdinalIgnoreCase),
                                                    "Random.Bool", null),
        new(n => Helpers.Like(n, "username") || Helpers.Like(n, "user_name"),
                                                    "Internet.UserName", null),
        new(n => Helpers.Like(n, "password") || Helpers.Like(n, "hash"),
                                                    "Internet.Password", null),
        new(n => Helpers.Like(n, "image") || Helpers.Like(n, "avatar") || Helpers.Like(n, "photo"),
                                                    "Internet.Avatar", null),
        new(n => Helpers.Like(n, "name"),                      "Name.FullName", null),
    ];
}

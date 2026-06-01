using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(13)]
public class M0013_SeedMaxConcurrentRequests : Migration
{
    public override void Up()
    {
        if (Schema.Table("settings").Exists())
        {
            Insert.IntoTable("settings").Row(new { key = "max_concurrent_requests", value = "1" });
        }
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "max_concurrent_requests" });
    }
}

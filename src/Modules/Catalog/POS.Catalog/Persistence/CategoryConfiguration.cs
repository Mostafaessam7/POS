using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POS.Catalog.Domain;

namespace POS.Catalog.Persistence;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(128).IsRequired();
        builder.Property(c => c.Slug).HasMaxLength(64).IsRequired();

        // 512 accommodates roughly eight levels of 64-character slugs. Deeper than
        // that and the hierarchy is a data-modelling problem, not an index problem.
        builder.Property(c => c.Path).HasMaxLength(512).IsRequired();

        // THE POINT OF THE MATERIALISED PATH. This index turns
        //   "every product in Menswear and below"
        // into a single indexed prefix seek:
        //   WHERE Path LIKE '/menswear/%'
        // instead of a recursive CTE on every catalogue page load.
        //
        // Note the leading slash on the stored path: without it, a LIKE '/audio%'
        // would also match '/audiobooks/'.
        builder.HasIndex(c => new { c.TenantId, c.Path })
               .HasFilter("[IsDeleted] = 0")
               .HasDatabaseName("IX_Categories_Path");

        builder.HasIndex(c => new { c.TenantId, c.Slug, c.ParentId })
               .IsUnique()
               .HasFilter("[IsDeleted] = 0")
               .HasDatabaseName("UX_Categories_Slug_Parent");

        builder.HasOne<Category>()
               .WithMany()
               .HasForeignKey(c => c.ParentId)
               // Restrict, not Cascade: deleting a category must not silently
               // delete the subtree and orphan every product beneath it.
               .OnDelete(DeleteBehavior.Restrict);
    }
}

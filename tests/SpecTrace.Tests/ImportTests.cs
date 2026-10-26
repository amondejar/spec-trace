using System;
using System.IO;
using System.Linq;
using SpecTrace.Specs;
using SpecTrace.Tool.Import;
using Xunit;

namespace SpecTrace.Tests
{
    public class ImportTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "spectrace-import-" + Guid.NewGuid().ToString("N"));

        public ImportTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private const string SpecKit = @"# Feature Specification: Photo albums

**Status**: Draft

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create an album (Priority: P1)

A user groups photos into an album.

**Why this priority**: Core of the feature.

**Acceptance Scenarios**:

1. **Given** 3 selected photos, **When** they create an album, **Then** it contains 3 photos.
2. **Given** no photos, **When** they try, **Then** the action is disabled.

### User Story 2 - Share an album (Priority: P3)

A user shares an album.

**Acceptance Scenarios**:

1. **Given** an album, **When** shared, **Then** [NEEDS CLARIFICATION: link or email?]

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST let users create albums with a name of 1 to 80 characters.
- **FR-002**: Users MUST be able to remove a photo from an album.
";

        private const string OpenSpec = @"# Auth Specification

## Requirements

### Requirement: User Authentication

The system SHALL issue a session token on valid sign-in.

#### Scenario: Valid credentials

- **GIVEN** a registered user
- **WHEN** they sign in with the right password
- **THEN** a session token is returned

### Requirement: Session Expiration

The system MUST expire idle sessions after 30 minutes.

#### Scenario: Idle session

- **WHEN** a session is idle for 31 minutes
- **THEN** the user must sign in again
";

        [Fact]
        public void SpecKit_user_stories_become_requirements_with_scenarios()
        {
            var source = Path.Combine(_dir, "001-photo-albums", "spec.md");
            var spec = SpecKitImporter.Parse(source, SpecKit, Path.Combine(_dir, "out.spec.md"));

            Assert.Equal("PA1", spec.SpecId);
            Assert.Equal("Photo albums", spec.Title);
            var us1 = spec.Requirements.Single(r => r.Id == "PA1-US-1");
            Assert.Equal("Create an album", us1.Title);
            Assert.Equal(SpecStatus.Approved, us1.Status);
            Assert.Equal("A user groups photos into an album. (Priority P1)", us1.Statement);
            Assert.Equal(2, us1.Criteria.Count);
            Assert.StartsWith("Given 3 selected photos, When they create an album", us1.Criteria[0]);

            Assert.Equal(SpecStatus.Proposed, spec.Requirements.Single(r => r.Id == "PA1-US-2").Status);

            var fr = spec.Requirements.Single(r => r.Id == "PA1-FR-002");
            Assert.Equal(SpecStatus.Proposed, fr.Status);
            Assert.Equal("Remove a photo from an album", fr.Title);
        }

        [Fact]
        public void SpecKit_output_is_a_valid_spectrace_spec()
        {
            var output = Path.Combine(_dir, "albums.spec.md");
            ImportWriter.Write(SpecKitImporter.Parse(Path.Combine(_dir, "001-photo-albums", "spec.md"), SpecKit, output));

            var doc = SpecParser.Parse(output, File.ReadAllText(output));
            Assert.Equal(new[] { "PA1-US-1", "PA1-US-2", "PA1-FR-001", "PA1-FR-002" }, doc.Requirements.Select(r => r.Id).ToArray());
            Assert.Equal("A user groups photos into an album. (Priority P1)", doc.Requirements[0].Statement);
            // FRs without criteria are proposed: they produce no format warnings.
            Assert.Empty(doc.Issues);
        }

        [Fact]
        public void Reimport_keeps_status_and_hand_written_criteria()
        {
            var source = Path.Combine(_dir, "001-photo-albums", "spec.md");
            var output = Path.Combine(_dir, "albums.spec.md");
            ImportWriter.Write(SpecKitImporter.Parse(source, SpecKit, output));

            // Someone approves FR-001 and adds a criterion by hand.
            // The spec is written with the system line ending (\r\n on Windows): normalize it before editing.
            var edited = File.ReadAllText(output).Replace("\r\n", "\n").Replace(
                "## PA1-FR-001: Let users create albums with a name of…\nStatus: proposed",
                "## PA1-FR-001: Let users create albums with a name of…\nStatus: approved");
            edited = edited.Replace("System MUST let users create albums with a name of 1 to 80 characters.",
                "System MUST let users create albums with a name of 1 to 80 characters.\n\n- AC-1: Given a name of 81 characters, then it is rejected.");
            File.WriteAllText(output, edited);

            var result = ImportWriter.Write(SpecKitImporter.Parse(source, SpecKit, output));

            Assert.Equal(4, result.Kept);
            var fr1 = SpecParser.Parse(output, File.ReadAllText(output)).Requirements.Single(r => r.Id == "PA1-FR-001");
            Assert.Equal(SpecStatus.Approved, fr1.Status);
            Assert.Single(fr1.Criteria);
        }

        [Fact]
        public void OpenSpec_requirements_and_scenarios_are_imported()
        {
            var spec = OpenSpecImporter.Parse("auth", "spec.md", OpenSpec, Path.Combine(_dir, "auth.spec.md"));

            Assert.Equal("AUTH", spec.SpecId);
            Assert.Equal(new[] { "AUTH-001", "AUTH-002" }, spec.Requirements.Select(r => r.Id).ToArray());
            var first = spec.Requirements[0];
            Assert.Equal("User Authentication", first.Title);
            Assert.Equal("The system SHALL issue a session token on valid sign-in.", first.Statement);
            Assert.Equal("Valid credentials: GIVEN a registered user, WHEN they sign in with the right password, THEN a session token is returned",
                Assert.Single(first.Criteria));
        }

        [Fact]
        public void OpenSpec_ids_are_stable_when_requirements_are_inserted()
        {
            var output = Path.Combine(_dir, "auth.spec.md");
            ImportWriter.Write(OpenSpecImporter.Parse("auth", "spec.md", OpenSpec, output));

            var withNewFirst = OpenSpec.Replace("### Requirement: User Authentication",
                "### Requirement: Password Reset\n\nThe system SHALL email a reset link.\n\n#### Scenario: Reset\n\n- **WHEN** a user asks for a reset\n- **THEN** an email is sent\n\n### Requirement: User Authentication");
            ImportWriter.Write(OpenSpecImporter.Parse("auth", "spec.md", withNewFirst, output));

            var ids = SpecParser.Parse(output, File.ReadAllText(output)).Requirements.ToDictionary(r => r.Title, r => r.Id);
            Assert.Equal("AUTH-001", ids["User Authentication"]);
            Assert.Equal("AUTH-002", ids["Session Expiration"]);
            Assert.Equal("AUTH-003", ids["Password Reset"]);
        }

        [Fact]
        public void OpenSpec_sources_skip_pending_changes()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "openspec", "specs", "auth"));
            Directory.CreateDirectory(Path.Combine(_dir, "openspec", "changes", "add-2fa", "specs", "auth"));
            File.WriteAllText(Path.Combine(_dir, "openspec", "specs", "auth", "spec.md"), OpenSpec);
            File.WriteAllText(Path.Combine(_dir, "openspec", "changes", "add-2fa", "specs", "auth", "spec.md"), "## ADDED Requirements");

            var sources = OpenSpecImporter.FindSources(Path.Combine(_dir, "openspec")).ToList();

            var (capability, _) = Assert.Single(sources);
            Assert.Equal("auth", capability);
        }

        [Theory]
        [InlineData("001-photo-albums", "PA1")]
        [InlineData("012-checkout", "C12")]
        [InlineData("search", "S")]
        public void SpecKit_prefix_is_derived_from_the_feature_folder(string folder, string expected)
        {
            Assert.Equal(expected, SpecKitImporter.DerivePrefix(folder));
        }
    }
}

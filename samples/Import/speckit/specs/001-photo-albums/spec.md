# Feature Specification: Photo albums

**Feature Branch**: `001-photo-albums`
**Created**: 2026-10-20
**Status**: Draft
**Input**: User description: "Let users group their photos into albums and reorder them"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create an album (Priority: P1)

A signed-in user groups selected photos into a new named album.

**Why this priority**: Albums are the core of the feature; nothing else works without them.

**Independent Test**: Create an album from three photos and check it appears in the album list.

**Acceptance Scenarios**:

1. **Given** a user with 3 selected photos, **When** they create an album named "Holidays", **Then** the album "Holidays" contains the 3 photos.
2. **Given** a user with no selected photos, **When** they try to create an album, **Then** the action is disabled.

### User Story 2 - Reorder albums (Priority: P2)

A user changes the order in which albums are shown by dragging them.

**Why this priority**: Useful once users have several albums.

**Independent Test**: Drag the third album to the first position and reload the page.

**Acceptance Scenarios**:

1. **Given** albums A, B and C, **When** the user drags C before A, **Then** the order is C, A, B after a reload.

### User Story 3 - Share an album (Priority: P3)

A user shares an album with people outside the app.

**Acceptance Scenarios**:

1. **Given** an album, **When** the user shares it, **Then** [NEEDS CLARIFICATION: public link or invitation by email?]

### Edge Cases

- What happens when a photo is deleted while it belongs to an album?

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST let users create albums with a name of 1 to 80 characters.
- **FR-002**: System MUST keep the album order per user across sessions.
- **FR-003**: Users MUST be able to remove a photo from an album without deleting the photo.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 90% of users create their first album in under 30 seconds.

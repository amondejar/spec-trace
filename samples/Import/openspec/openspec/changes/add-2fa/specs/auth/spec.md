## ADDED Requirements

### Requirement: Two-Factor Authentication

The system SHALL ask for a one-time code after the password when 2FA is enabled.

#### Scenario: Code required

- **WHEN** a user with 2FA signs in with the right password
- **THEN** they are asked for a one-time code

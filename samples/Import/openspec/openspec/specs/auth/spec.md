# Auth Specification

## Purpose

Authentication and session handling for the web app.

## Requirements

### Requirement: User Authentication

The system SHALL issue a session token when a user signs in with valid credentials.

#### Scenario: Valid credentials

- **GIVEN** a registered user
- **WHEN** they sign in with the right password
- **THEN** a session token is returned
- **AND** they are redirected to the dashboard

#### Scenario: Wrong password

- **WHEN** a user signs in with a wrong password
- **THEN** no token is issued and an error is shown

### Requirement: Session Expiration

The system MUST expire sessions after 30 minutes of inactivity.

#### Scenario: Idle session

- **GIVEN** a session idle for 31 minutes
- **WHEN** the user makes a request
- **THEN** they are asked to sign in again

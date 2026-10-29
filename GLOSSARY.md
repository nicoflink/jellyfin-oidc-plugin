# Jellyfin OIDC Plugin

A Jellyfin plugin that lets people sign in to Jellyfin through an external OpenID Connect provider, so that passkey-only providers like PocketID become the way into Jellyfin.

## Parties

**Identity Provider**:
The single external OpenID Connect service that authenticates people and vouches for who they are (PocketID in our setup).
_Avoid_: IdP server, auth server, SSO server

**Plugin**:
This Jellyfin plugin, acting as the OpenID Connect Relying Party on behalf of Jellyfin.
_Avoid_: client, RP, SSO plugin

**Passkey**:
A WebAuthn credential a person uses to authenticate at the Identity Provider; the Plugin never sees or handles it.
_Avoid_: security key, FIDO token

## Identity

**Subject**:
The stable identifier of a person at the Identity Provider: the `sub` claim, qualified by the issuer that issued it.
_Avoid_: user id, external id

**Linked Identity**:
The association between one Subject and one Jellyfin user; the only basis on which an OIDC login resolves to a Jellyfin user.
_Avoid_: mapping, binding, account link

**Just-in-time Provisioning**:
Creating a Jellyfin user, and its Linked Identity, at the first OIDC login of a Subject that has none.
_Avoid_: auto-registration, sign-up

**Admin Group**:
The Identity Provider group whose members are granted Jellyfin administrator rights, re-evaluated at every OIDC login.
_Avoid_: admin role, admin claim

## Signing in

**OIDC Login**:
Signing in to Jellyfin's web UI by being redirected to the Identity Provider and back.
_Avoid_: SSO login, external login

**Local Login**:
Jellyfin's built-in username-and-password sign-in, always kept available as a fallback.
_Avoid_: password login, break-glass login

**Login Entry Point**:
The place in Jellyfin's web UI where a person starts an OIDC Login.
_Avoid_: SSO button, login link

**Quick Connect**:
Jellyfin's built-in mechanism for signing in a native or TV app by approving a code from an already signed-in session.

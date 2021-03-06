/**
 * @module botbuilder
 */
/**
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License.
 */

// import { IChatConnectorAddress } from '../ChatConnector'; 
import { AppCredentials } from './appCredentials';
import { AuthenticationConfiguration } from './authenticationConfiguration';
import { AuthenticationConstants } from './authenticationConstants';
import { ChannelValidation } from './channelValidation';
import { Claim, ClaimsIdentity } from './claimsIdentity';
import { ICredentialProvider } from './credentialProvider';
import { EmulatorValidation } from './emulatorValidation';
import { SkillValidation } from './skillValidation';

export namespace JwtTokenValidation {

    /**
     * Authenticates the request and sets the service url in the set of trusted urls.
     * @param  {IMessage} activity The incoming Activity from the Bot Framework or the Emulator
     * @param  {string} authHeader The Bearer token included as part of the request
     * @param  {ICredentialProvider} credentials The set of valid credentials, such as the Bot Application ID
     * @returns {Promise<ClaimsIdentity>} Promise with ClaimsIdentity for the request.
     */
    export async function authenticateRequest(
        activity: IMessage,
        authHeader: string,
        credentials: ICredentialProvider,
        serviceUrl: string,
        authConfig?: AuthenticationConfiguration,
    ): Promise<ClaimsIdentity> {
        if (!authConfig) {
            authConfig = new AuthenticationConfiguration();
        }

        if (!authHeader.trim()) {
            const isAuthDisabled: boolean = await credentials.isAuthenticationDisabled();

            if (isAuthDisabled) {
                return new ClaimsIdentity([], true);
            }

            throw new Error('Unauthorized Access. Request is not authorized');
        }

        const claimsIdentity: ClaimsIdentity =
            await validateAuthHeader(authHeader, credentials, activity.address.channelId, serviceUrl, authConfig);

        AppCredentials.trustServiceUrl(serviceUrl);

        return claimsIdentity;
    }

    export async function validateAuthHeader(
        authHeader: string,
        credentials: ICredentialProvider,
        channelId: string,
        serviceUrl: string = '',
        authConfig: AuthenticationConfiguration = new AuthenticationConfiguration()
    ): Promise<ClaimsIdentity> {
        if (!authHeader.trim()) { throw new Error('\'authHeader\' required.'); }

        const identity = await authenticateToken(authHeader, credentials, channelId, authConfig, serviceUrl);

        await validateClaims(authConfig, identity.claims);

        return identity;
    }

    async function authenticateToken(
        authHeader: string, credentials: ICredentialProvider, channelId: string, authConfig: AuthenticationConfiguration, serviceUrl: string): Promise<ClaimsIdentity> {

            if (SkillValidation.isSkillToken(authHeader)) {
                return await SkillValidation.authenticateChannelToken(authHeader, credentials, channelId, authConfig);
            }

            const usingEmulator: boolean = EmulatorValidation.isTokenFromEmulator(authHeader);

            if (usingEmulator) {
                return await EmulatorValidation.authenticateEmulatorToken(authHeader, credentials, channelId);
            }
    
            if (serviceUrl.trim()) {
                return await ChannelValidation.authenticateChannelTokenWithServiceUrl(authHeader, credentials, serviceUrl, channelId);
            }

            return await ChannelValidation.authenticateChannelToken(authHeader, credentials, channelId);
    }

    /**
     * Validates the identity claims against the ClaimsValidator in AuthenticationConfiguration if present. 
     * @param authConfig 
     * @param claims The list of claims to validate.
     */
    async function validateClaims(authConfig: AuthenticationConfiguration, claims: Claim[] = []): Promise<void> {
        if (!authConfig.validateClaims) {
            throw new Error(`JwtTokenValidation.ValidateClaimsAsync.authConfig must have a ClaimsValidator.`);
        }
        // Call the validation method (it should throw an exception if the validation fails)
        await authConfig.validateClaims(claims);
    }

    /**
     * Gets the AppId from a claims list.
     * @remarks
     * In v1 tokens the AppId is in the "ver" AuthenticationConstants.AppIdClaim claim.
     * In v2 tokens the AppId is in the "azp" AuthenticationConstants.AuthorizedParty claim.
     * If the AuthenticationConstants.VersionClaim is not present, this method will attempt to
     * obtain the attribute from the AuthenticationConstants.AppIdClaim or if present.
     * 
     * Throws a TypeError if claims is falsy.
     * @param claims An object containing claims types and their values.
     */
    export function getAppIdFromClaims(claims: Claim[]): string {
        if (!claims) {
            throw new TypeError(`JwtTokenValidation.getAppIdFromClaims(): missing claims.`);
        }
        let appId: string;

        // Depending on Version, the AppId is either in the
        // appid claim (Version 1) or the 'azp' claim (Version 2).
        const versionClaim = claims.find((c: any) => c.type === AuthenticationConstants.VersionClaim);
        const versionValue = versionClaim && versionClaim.value;
        if (!versionValue || versionValue === '1.0') {
            // No version or a version of '1.0' means we should look for
            // the claim in the 'appid' claim.
            const appIdClaim = claims.find((c: any) => c.type === AuthenticationConstants.AppIdClaim);
            appId = appIdClaim && appIdClaim.value;
        } else if (versionValue === '2.0') {
            // Version '2.0' puts the AppId in the 'azp' claim.
            const azpClaim = claims.find((c: any) => c.type === AuthenticationConstants.AuthorizedParty);
            appId = azpClaim && azpClaim.value;
        }

        return appId;
    }

    /**
     * Internal helper to check if the token has the shape we expect "Bearer [big long string]".
     * @param authHeader A string containing the token header.
     * @returns {boolean} True if the token is valid, false if not.
     */
    export function isValidTokenFormat(authHeader: string): boolean {
        if (!authHeader) {
            // No token, not valid.
            return false;
        }

        const parts: string[] = authHeader.trim().split(' ');
        if (parts.length !== 2) {
            // Tokens MUST have exactly 2 parts. If we don't have 2 parts, it's not a valid token
            return false;
        }

        // We now have an array that should be:
        // [0] = "Bearer"
        // [1] = "[Big Long String]"
        const authScheme: string = parts[0];
        if (authScheme !== 'Bearer') {
            // The scheme MUST be "Bearer"
            return false;
        }

        return true;
    }
}

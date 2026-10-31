// The Complete page: exchanges the handoff code for a Jellyfin session, hands it to jellyfin-web the way its own
// sign-in stores it, and opens the web home. The fragment and the response are treated strictly as data.
(function () {
    'use strict';

    // The same format the Complete endpoint accepts.
    var DEVICE_ID = /^[A-Za-z0-9+/=_-]{1,512}$/;

    // jellyfin-web's ConnectionMode.Manual.
    var MANUAL_CONNECTION = 2;

    var status = document.getElementById('status');

    // The server address jellyfin-web uses: the Public Jellyfin URL including any base path, without a trailing slash.
    var serverAddress = new URL('..', window.location.href).href.replace(/\/$/, '');

    function show(text) {
        status.textContent = text;
    }

    // jellyfin-web's own device id (apphost.js getDeviceId), so this browser keeps one device and a new OIDC Login
    // replaces its earlier session.
    function getDeviceId() {
        var deviceId = localStorage.getItem('_deviceId2');
        if (!deviceId || !DEVICE_ID.test(deviceId)) {
            deviceId = btoa([navigator.userAgent, new Date().getTime()].join('|')).replaceAll('=', '1');
            localStorage.setItem('_deviceId2', deviceId);
        }

        return deviceId;
    }

    // Writes the session into jellyfin-web's credential store (jellyfin-apiclient's Credentials), as the server it
    // last used, so it starts signed in.
    function storeCredentials(session) {
        var credentials;
        try {
            credentials = JSON.parse(localStorage.getItem('jellyfin_credentials'));
        } catch (e) {
            credentials = null;
        }

        if (!credentials || !Array.isArray(credentials.Servers)) {
            credentials = { Servers: [] };
        }

        var server = credentials.Servers.find(function (s) {
            return s && s.Id === session.serverId;
        });
        if (!server) {
            server = { Id: session.serverId };
            credentials.Servers.push(server);
        }

        server.AccessToken = session.accessToken;
        server.UserId = session.userId;
        server.ManualAddress = serverAddress;
        server.LastConnectionMode = MANUAL_CONNECTION;
        server.manualAddressOnly = true;
        server.DateLastAccessed = new Date().getTime();
        localStorage.setItem('jellyfin_credentials', JSON.stringify(credentials));
    }

    var code = new URLSearchParams(window.location.hash.slice(1)).get('code');
    window.history.replaceState(null, '', window.location.pathname + window.location.search);
    if (!code) {
        show('Sign-in Expired.');
        return;
    }

    fetch('Complete', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ code: code, deviceId: getDeviceId() })
    }).then(function (response) {
        return response.json().then(function (body) {
            if (!response.ok) {
                show('Sign-in ' + String(body.category) + '.');
                return;
            }

            storeCredentials(body);
            window.location.replace(serverAddress + '/web/');
        });
    }).catch(function () {
        show('Sign-in Failed.');
    });
}());

// Sign in with Apple, natively.
//
// Apple's web OAuth flow needs a client secret — a JWT signed with a private
// key downloaded from the developer portal — and anything shipped inside an app
// is not secret. So the web flow, which is what Google's sign-in here uses, is
// not available: the correct implementation on iOS is ASAuthorization, and this
// is the smallest amount of Objective-C that provides it.
//
// The nonce arrives already hashed. Unity keeps the original and sends it to
// Firebase; Apple receives only the SHA-256 and puts it inside the signed
// token. Firebase compares the two. That is what stops a token captured from
// one sign-in being replayed into another, and it is why the hashing happens on
// the Unity side rather than here.
//
// The result goes back through UnitySendMessage, which is the only channel a
// native plugin has into a Unity scene. The object name below must match the
// GameObject AppleSignIn lives on.

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <AuthenticationServices/AuthenticationServices.h>

extern void UnitySendMessage(const char *object, const char *method, const char *message);

static NSString *const kUnityObject = @"MarkerOne Apple";

API_AVAILABLE(ios(13.0))
@interface MarkerOneAppleAuth : NSObject <ASAuthorizationControllerDelegate,
                                          ASAuthorizationControllerPresentationContextProviding>
@end

@implementation MarkerOneAppleAuth

// Held for the life of the request. Without a strong reference the delegate is
// deallocated the moment this returns and the callbacks never arrive.
static MarkerOneAppleAuth *_current = nil;

- (void)startWithNonce:(NSString *)hashedNonce
{
    ASAuthorizationAppleIDProvider *provider = [[ASAuthorizationAppleIDProvider alloc] init];
    ASAuthorizationAppleIDRequest *request = [provider createRequest];

    // Full name is requested because Apple gives it exactly once, on the first
    // sign-in for an account, and never again. Asking later is not possible.
    request.requestedScopes = @[ASAuthorizationScopeEmail, ASAuthorizationScopeFullName];
    request.nonce = hashedNonce;

    ASAuthorizationController *controller =
        [[ASAuthorizationController alloc] initWithAuthorizationRequests:@[request]];

    controller.delegate = self;
    controller.presentationContextProvider = self;
    [controller performRequests];
}

- (void)authorizationController:(ASAuthorizationController *)controller
   didCompleteWithAuthorization:(ASAuthorization *)authorization
{
    id credential = authorization.credential;
    if (![credential isKindOfClass:[ASAuthorizationAppleIDCredential class]])
    {
        UnitySendMessage([kUnityObject UTF8String], "OnAppleFailed",
                         "unexpected credential type");
        _current = nil;
        return;
    }

    ASAuthorizationAppleIDCredential *apple = credential;
    NSString *token = [[NSString alloc] initWithData:apple.identityToken
                                            encoding:NSUTF8StringEncoding];

    if (token.length == 0)
    {
        UnitySendMessage([kUnityObject UTF8String], "OnAppleFailed",
                         "no identity token");
        _current = nil;
        return;
    }

    UnitySendMessage([kUnityObject UTF8String], "OnAppleToken", [token UTF8String]);
    _current = nil;
}

- (void)authorizationController:(ASAuthorizationController *)controller
           didCompleteWithError:(NSError *)error
{
    // Cancelling is a choice rather than a failure, and is reported as such so
    // the interface does not show somebody an error for changing their mind.
    NSString *why = (error.code == ASAuthorizationErrorCanceled)
        ? @"cancelled"
        : (error.localizedDescription ?: @"unknown error");

    UnitySendMessage([kUnityObject UTF8String], "OnAppleFailed", [why UTF8String]);
    _current = nil;
}

- (ASPresentationAnchor)presentationAnchorForAuthorizationController:
    (ASAuthorizationController *)controller
{
    return UIApplication.sharedApplication.keyWindow;
}

@end

void MarkerOneAppleSignIn(const char *hashedNonce)
{
    if (@available(iOS 13.0, *))
    {
        NSString *nonce = hashedNonce ? [NSString stringWithUTF8String:hashedNonce] : @"";

        _current = [[MarkerOneAppleAuth alloc] init];
        [_current startWithNonce:nonce];
    }
    else
    {
        UnitySendMessage([kUnityObject UTF8String], "OnAppleFailed",
                         "Sign in with Apple needs iOS 13 or newer");
    }
}

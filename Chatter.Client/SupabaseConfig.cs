namespace Chatter.Client;

// Supabase's anon/publishable key is meant to be shipped inside client apps - it's the public
// key browsers and mobile apps use, and access control is enforced by Row Level Security on
// the server, not by keeping this value secret. It's centralized here (instead of inline in
// MauiProgram's DI setup) so there's one obvious place to update if this app ever points at a
// different Supabase project.
public static class SupabaseConfig
{
    public const string Url = "https://bvzbuxxskzodjvqflgbv.supabase.co";
    public const string AnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImJ2emJ1eHhza3pvZGp2cWZsZ2J2Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NjA2MDc1OTcsImV4cCI6MjA3NjE4MzU5N30.nL2p01tukPkUVRD2AXQo1s1aHg_JZIaBS-BvwzEmp3g";
}

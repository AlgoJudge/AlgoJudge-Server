import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import './App.css';

import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { RouterProvider, createBrowserRouter } from 'react-router-dom';
import Layout from './Layout';
import Home from './pages/home/Home';
import Login from './pages/login/Login';
import Manage from './pages/manage/Manage';
import Register from './pages/register/Register';
import Authentication from './routers/Authentication';
import AppLayout from './layouts/app/AppLayout';
import ActivitiesPage from './pages/activities/ActivitiesPage';
import ProblemsPage from './pages/activity/problems/ProblemsPage';
import SubmitPage from './pages/activity/submit/SubmitPage';
import SubmissionsPage from './pages/activity/submissions/SubmissionsPage';
import RankingPage from './pages/activity/ranking/RankingPage';
import QuestionsPage from './pages/activity/questions/QuestionsPage';
import RulesPage from './pages/activity/rules/RulesPage';
import ProblemPage from './pages/activity/problem/ProblemPage';
import SubmissionPage from './pages/activity/submission/SubmissionPage';
import CodePage from './pages/activity/submission/code/CodePage';

function App() {

    const router = createBrowserRouter([
        {
            path: "/",
            element: <Layout />,
            children: [
                {
                    path: "/",
                    element: <Home />
                },
                {
                    path: "/login",
                    element: <Login />
                },
                {
                    path: "/register",
                    element: <Register />
                },
                {
                    path: "/manage",
                    element: <Authentication><Manage /></Authentication>
                }
            ]
        },
        {
            path: "/",
            element: <AppLayout />,
            children: [
                {
                    path: "/activities",
                    element: <ActivitiesPage />
                },
                {
                    path: "/activity/:activityId/problems",
                    element: <ProblemsPage />
                },
                {
                    path: "/activity/:activityId/submit/:problemId?",
                    element: <SubmitPage />
                },
                {
                    path: "/activity/:activityId/submissions",
                    element: <SubmissionsPage />
                },
                {
                    path: "/activity/:activityId/ranking",
                    element: <RankingPage />
                },
                {
                    path: "/activity/:activityId/questions",
                    element: <QuestionsPage />
                },
                {
                    path: "/activity/:activityId/rules",
                    element: <RulesPage />
                },
                {
                    path: "/activity/:activityId/problem/:problemId",
                    element: <ProblemPage />
                },
                {
                    path: "/activity/:activityId/submission/:submissionId",
                    element: <SubmissionPage />
                },
                {
                    path: "/activity/:activityId/submission/:submissionId/code",
                    element: <CodePage />
                }
            ]
        }
    ], { basename: import.meta.env.BASE_URL });

    return (
            <MantineProvider>
                <Notifications />
                <RouterProvider router={router} />
            </MantineProvider>
    );
}

export default App;
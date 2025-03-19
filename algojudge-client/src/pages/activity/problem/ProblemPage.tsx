import { Title } from "@mantine/core";
import classes from "./ProblemPage.module.css"
import { useTranslation } from "react-i18next";

interface Model {
    /* ... */
}

const data: Model = { /* ... */};

export default function ProblemPage() {
    const { t } = useTranslation();
    return (
        <>
            <Title>{t("Problem")}</Title>
        </>
    );
}
